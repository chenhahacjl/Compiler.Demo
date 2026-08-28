using Cocoa.CodeAnalysis.Emit.IL;
using Cocoa.CodeAnalysis.Lowering;
using Cocoa.CodeAnalysis.Cod;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定器
    /// </summary>
    internal sealed partial class Binder
    {
        private readonly DiagnosticBag _diagnostics = new DiagnosticBag();
        private readonly bool _isScript;
        private readonly FunctionSymbol? _function;
        private readonly NamedTypeSymbol? _currentClass;
        private readonly string[] _references;
        private readonly Language _language;

        private readonly List<string> _usingNamespaces = new List<string>();
        private readonly List<string> _usingStatics = new List<string>();
        private readonly Dictionary<string, string> _usingAliases = new Dictionary<string, string>();
        private readonly ImmutableArray<CodProgram> _codLibraries;

        /// <summary>全局命名空间树（Phase 1-5：声明阶段后才可用——树由已完成的全局作用域构建）。
        /// 仅在函数体绑定阶段注入；`FindDeclaredClassByFullName/Enum` 优先用它做全名/using 定位，
        /// 未命中回退作用域链（树只索引全局静态声明，动态/单态化/委托类型等以回退兜底）。</summary>
        private readonly NamespaceSymbol? _globalNamespace;

        private Stack<(BoundLabel BreakLabel, BoundLabel ContinueLabel)> _loopStack = new Stack<(BoundLabel BreakLabel, BoundLabel ContinueLabel)>();
        private int _labelCounter;
        private BoundScope _scope;

        /// <summary>
        /// 类型实参名映射（6e-M20 单态化）：Monomorphizer 以实例化方法为容器重绑泛型定义语法时，
        /// 把类型参数名（T/U…）解析到具体实参。
        /// </summary>
        private readonly Dictionary<string, TypeSymbol> _typeArgumentsByName = new Dictionary<string, TypeSymbol>();

        /// <summary>类/接口声明绑定上下文（6e-M20）：成员签名/基类/约束解析期间的类型参数来源。</summary>
        private NamedTypeSymbol? _bindingClass;

        /// <summary>泛型方法签名绑定上下文（6e-M20）：BindFunctionDeclaration / 类方法 / 接口成员签名期间的 T 解析。</summary>
        private ImmutableArray<TypeParameterSymbol> _declaringMethodTypeParameters = ImmutableArray<TypeParameterSymbol>.Empty;

        /// <summary>lambda 提升全局序号（6e-M22 C4）：进程内单调，保证合成名 `__Lambda$N` 唯一。</summary>
        private static int _lambdaGlobalSequence;

        /// <summary>环境对象宿主函数（6e-M22 C5）：当前绑定上下文的捕获变量承载者；lambda 继承最外层非 lambda 函数。</summary>
        private FunctionSymbol? _environmentOwner;

        /// <summary>环境类缓存（6e-M22 C5）：每宿主函数一个合成 `__Env_&lt;fn&gt;` 类。</summary>
        private readonly Dictionary<FunctionSymbol, NamedTypeSymbol> _environmentClasses = new();

        /// <summary>lambda 体绑定深度（6e-M22 C5）：>0 时返回语句按推断语义处理（不套外层签名转换）。</summary>
        private int _lambdaBodyDepth;

        private bool IsBindingLambdaBody() => _lambdaBodyDepth > 0;

        /// <summary>设置声明绑定上下文（BindGlobalScope 阶段 3/3.2/3.5 调用）。</summary>
        internal void SetBindingClass(NamedTypeSymbol? classType) => _bindingClass = classType;

        internal Binder(bool isScript, BoundScope? parent, FunctionSymbol? function, ImmutableArray<string> references, ImmutableArray<string> usingNamespaces, Language dialect, ImmutableArray<string> usingStatics = default, ImmutableDictionary<string, string> usingAliases = null, ImmutableArray<CodProgram> codLibraries = default, NamespaceSymbol? globalNamespace = null)
        {
            _scope = new BoundScope(parent);
            _isScript = isScript;
            _function = function;
            _currentClass = function?.ContainingClass;
            _references = references.ToArray();
            _language = dialect;
            _codLibraries = codLibraries.IsDefault ? ImmutableArray<CodProgram>.Empty : codLibraries;
            _globalNamespace = globalNamespace;
            _usingNamespaces.AddRange(usingNamespaces);
            if (!usingStatics.IsDefaultOrEmpty)
            {
                _usingStatics.AddRange(usingStatics);
            }
            if (usingAliases != null)
            {
                foreach (var (alias, target) in usingAliases)
                {
                    _usingAliases[alias] = target;
                }
            }

            if (function != null)
            {
                foreach (var parameter in function.Parameters)
                {
                    _scope.TryDeclareVariable(parameter);
                }
            }
        }

        public static BoundGlobalScope BindGlobalScope(bool isScript, BoundGlobalScope? previous, ImmutableArray<SyntaxTree> syntaxTrees, string entryPointName = "Main", string[]? references = null, ImmutableArray<CodProgram> codLibraries = default)
        {
            // 6e-M19 M2-c：System.Object 成员面注入（幂等）——须先于类成员绑定，
            // 用户类 override 解析与成员沿链上溯依赖 Object 四虚方法已就位
            SystemObjectMembers.Ensure();

            var parentScope = CreateParentScope(previous);
            InjectCodSymbols(parentScope, codLibraries);

            // 6e-M22 C5+：内建 Delegate/MulticastDelegate 注册进根作用域（类型位置可用）
            parentScope.TryDeclareClass(NamedTypeSymbol.SystemDelegate);
            parentScope.TryDeclareClass(NamedTypeSymbol.SystemMulticastDelegate);

            var language = syntaxTrees.IsDefaultOrEmpty ? Language.Cocoa : syntaxTrees[0].Language;
            var binder = new Binder(isScript, parentScope, null, references?.ToImmutableArray() ?? ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, language, codLibraries: codLibraries);

            binder.Diagnostics.AddRange(syntaxTrees.SelectMany(st => st.Diagnostics));
            if (binder.Diagnostics.HasErrors())
            {
                return new BoundGlobalScope(previous, binder.Diagnostics.ToImmutableArray(), null, null, ImmutableArray<FunctionSymbol>.Empty, ImmutableArray<NamedTypeSymbol>.Empty, ImmutableArray<NamedTypeSymbol>.Empty, ImmutableArray<VariableSymbol>.Empty, ImmutableArray<BoundStatement>.Empty, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, ImmutableDictionary<string, string>.Empty, (references ?? Array.Empty<string>()).ToImmutableArray());
            }

            var globalStatements = syntaxTrees.SelectMany(st => st.Root.Members)
                                              .OfType<GlobalStatementSyntax>();

            string? importedDll = null;

            var classFunctions = new List<FunctionSymbol>();
            var allClasses = new List<(ClassDeclarationSyntax Syntax, string Namespace)>();
            var pendingDelegates = new List<(DelegateDeclarationSyntax Syntax, string Namespace)>();
            var allInterfaces = new List<(InterfaceDeclarationSyntax Syntax, string Namespace)>();
            var allEnums = new List<(EnumDeclarationSyntax Syntax, string Namespace)>();
            var pendingFunctions = new List<(FunctionDeclarationSyntax Syntax, string Namespace, string? Dll)>();
            var usingDirectives = new List<UsingDirectiveSyntax>();

            // 阶段 1：处理 import/function/enum/using + 收集所有类/接口/枚举声明（递归 namespace）
            foreach (var member in syntaxTrees.SelectMany(st => st.Root.Members))
            {
                if (member is ImportClauseSyntax importClause)
                {
                    importedDll = importClause.DllName;
                }
                else if (member is FunctionDeclarationSyntax function)
                {
                    // 函数签名延后到阶段 2.5（接口声明）之后绑定：签名类型可引用接口。
                    // 快照当前 import DLL —— 多个 import 声明各自对应其后的 extern 函数
                    pendingFunctions.Add((function, "", importedDll));
                }
                else if (member is EnumDeclarationSyntax enumDeclaration)
                {
                    allEnums.Add((enumDeclaration, ""));
                }
                else if (member is ClassDeclarationSyntax classDeclaration)
                {
                    allClasses.Add((classDeclaration, ""));
                }
                else if (member is DelegateDeclarationSyntax delegateDeclaration)
                {
                    pendingDelegates.Add((delegateDeclaration, ""));
                }
                else if (member is InterfaceDeclarationSyntax interfaceDeclaration)
                {
                    allInterfaces.Add((interfaceDeclaration, ""));
                }
                else if (member is NamespaceDeclarationSyntax namespaceDeclaration)
                {
                    binder.CollectClasses(namespaceDeclaration, "", allClasses);
                    binder.CollectInterfaces(namespaceDeclaration, "", allInterfaces);
                    binder.CollectEnums(namespaceDeclaration, "", allEnums);
                    binder.CollectNamespaceFunctions(namespaceDeclaration, "", importedDll, pendingFunctions);
                    binder.CollectNamespaceUsings(namespaceDeclaration, binder._usingNamespaces, usingDirectives);
                }
                else if (member is UsingDirectiveSyntax usingDirective)
                {
                    binder.CollectUsingDirective(usingDirective);
                    usingDirectives.Add(usingDirective);
                }
            }

            // 阶段 1.4：using 命名空间解析警告（6e-M15）——在程序/引用/.cod 库中都找不到时发警告
            binder.ReportUnresolvedUsings(usingDirectives, allClasses, allInterfaces, allEnums, pendingFunctions, codLibraries);

            // 阶段 1.5：绑定枚举（顶层 + 命名空间内）
            foreach (var (syntax, ns) in allEnums)
            {
                binder.BindEnumDeclaration(syntax, ns);
            }

            // 阶段 2：声明所有类壳（部分类按全名分组合并为同一符号；两阶段：类可前向引用基类）
            var classGroups = new List<(NamedTypeSymbol Type, List<(ClassDeclarationSyntax Syntax, string Namespace)> Parts)>();
            var classByName = new Dictionary<string, List<(ClassDeclarationSyntax Syntax, string Namespace)>>();

            foreach (var (syntax, ns) in allClasses)
            {
                var fullName = ns.Length == 0 ? syntax.Identifier.Text : ns + "." + syntax.Identifier.Text;
                if (!classByName.TryGetValue(fullName, out var parts))
                {
                    parts = new List<(ClassDeclarationSyntax Syntax, string Namespace)>();
                    classByName.Add(fullName, parts);
                }

                parts.Add((syntax, ns));
            }

            foreach (var parts in classByName.Values)
            {
                classGroups.Add((binder.DeclareClassGroup(parts), parts));
            }

            // 阶段 2.5：声明接口（先于类，类可实现后声明的接口）
            var interfaceSymbols = new List<(InterfaceDeclarationSyntax Syntax, string Namespace, NamedTypeSymbol Symbol)>();
            foreach (var (syntax, ns) in allInterfaces)
            {
                var symbol = binder.DeclareInterfaceSymbol(syntax, ns);
                interfaceSymbols.Add((syntax, ns, symbol));
            }

            // 阶段 3：绑定接口（基接口 + 抽象成员）→ 泛型约束（6e-M20 阶段 3.2）→ 类成员 → 接口实现完整性检查
            foreach (var (syntax, ns, symbol) in interfaceSymbols)
            {
                binder.SetBindingClass(symbol);
                binder.BindInterfaceDeclaration(syntax, symbol, classFunctions);
            }

            binder.SetBindingClass(null);

            // 阶段 3.1：绑定 delegate 声明（6e-M22 D-A）——合成 sealed class extends MulticastDelegate
            foreach (var (delegateSyntax, delegateNs) in pendingDelegates)
            {
                binder.BindTopLevelDelegateDeclaration(delegateSyntax, delegateNs);
            }

            // 阶段 3.2：类泛型 where 约束解析（6e-M20；接口/类符号均已声明）
            foreach (var (classType, parts) in classGroups)
            {
                binder.BindClassWhereClauses(parts, classType);
            }

            // 阶段 3.5：绑定类成员（字段/方法/构造/基类）——部分类每个部分分别绑定，隐式默认构造在所有部分之后统一生成
            foreach (var (classType, parts) in classGroups)
            {
                var primary = parts[0].Syntax;

                // 6e-M19 M2-b → 6e-M20 v3：facade 类标记改为显式 `facade` 修饰符驱动——
                // 命中 FacadeTargets 且带标记 → 认领；命中但无标记 → 警告（按普通类处理）；
                // 须先于成员绑定，实例方法声明时的降级依赖此标记
                var declaredFacade = primary.Modifiers.Any(m => m.Kind == SyntaxKind.FacadeKeyword);
                if (FacadeTargets.TryGetValue(classType.FullName, out var facadeTarget))
                {
                    if (declaredFacade)
                    {
                        classType.IsFacadeClass = true;
                        classType.FacadeThisType = facadeTarget;
                    }
                    else
                    {
                        binder.Diagnostics.ReportFacadeMarkerRecommended(primary.Identifier.Location, classType.FullName, facadeTarget.Name);
                    }
                }

                // 3.5a：先落位全部部分的显式基类（部分类一致性检查 + 循环继承检测）
                foreach (var (baseSyntax, _) in parts)
                {
                    binder.BindClassBase(baseSyntax, classType);
                }

                // 6e-M19 M2-c 前移：无显式基类的非接口类默认继承 System.Object——
                // 须先于成员绑定，override 签名解析/base 表达式/成员沿链上溯依赖基类链就位（接口不默认）。
                // facade struct 无 CO 基类（整类映射到 BCL 值类型），跳过默认 Object 基类。
                if (!classType.IsInterface && classType.BaseType == null &&
                    !primary.Modifiers.Any(m => m.Kind == SyntaxKind.FacadeKeyword))
                {
                    classType.BaseType = NamedTypeSymbol.SystemObject;
                }

                // 6e-M26 Phase3：facade struct → 整类映射到 BCL 值类型（FullName 即 BCL 全名，对齐 facade class 约定）：
                // 不发射 CO TypeDef，类型/成员调用重定向到 BCL（this 为 BCL 值类型，按托管指针传参）。
                // 可选基类子句（单标识符）作为显式映射目标；缺省则直接用 FullName 解析 BCL 类型。
                if (classType.TypeKind == TypeKind.Struct && primary.Modifiers.Any(m => m.Kind == SyntaxKind.FacadeKeyword))
                {
                    classType.IsFacadeClass = true;
                    if (classType.BaseType != null)
                    {
                        if (!classType.BaseType.IsValueType)
                        {
                            binder.Diagnostics.ReportError(primary.Identifier.Location, $"facade struct '{classType.Name}' 的基类 '{classType.BaseType.Name}' 必须是值类型（BCL struct）。");
                        }
                        else
                        {
                            classType.FacadeThisType = classType.BaseType;
                        }
                    }
                }

                // 3.5b：成员绑定
                foreach (var (syntax, ns) in parts)
                {
                    binder.BindClassMembers(syntax, classType, classFunctions, ns);
                }

                binder.DeclareImplicitConstructor(classType, classFunctions, primary);
                binder.DeclareImplicitStaticConstructor(classType, classFunctions, primary);
            }

            // 阶段 4：接口实现完整性检查（类须实现其所有接口的全部成员）
            foreach (var (classType, parts) in classGroups)
            {
                binder.CheckInterfaceImplementation(classType);
            }

// 阶段 4.5：绑定全局函数签名（类型可引用接口/类）
            foreach (var (function, ns, dll) in pendingFunctions)
            {
                binder.BindFunctionDeclaration(function, ns, dll);
            }

            var statements = ImmutableArray.CreateBuilder<BoundStatement>();

            foreach (var globalStatement in globalStatements)
            {
                var statement = binder.BindGlobalStatement(globalStatement.Statement);

                statements.Add(statement);
            }

            // Check global statements

            var firstGlobalStatementPerSyntaxTree = syntaxTrees
                .Select(st => st.Root.Members.OfType<GlobalStatementSyntax>().FirstOrDefault())
                .Where(g => g != null)
                .Select(g => g!)
                .ToArray();

            if (firstGlobalStatementPerSyntaxTree.Length > 1)
            {
                foreach (var globalStatement in firstGlobalStatementPerSyntaxTree)
                {
                    binder.Diagnostics.ReportOnlyOneFileCanHaveGlobalStatements(globalStatement.Location);
                }
            }

            // Check for main/script with global statements

            var functions = binder._scope.GetDeclaredFunctions();
            if (classFunctions.Count > 0)
            {
                functions = functions.AddRange(classFunctions);
            }

            FunctionSymbol? mainFunction;
            FunctionSymbol? scriptFunction;

            if (isScript)
            {
                mainFunction = null;

                if (globalStatements.Any())
                {
                    scriptFunction = new FunctionSymbol("$eval", ImmutableArray<ParameterSymbol>.Empty, TypeSymbol.Any, null);
                }
                else
                {
                    scriptFunction = null;
                }
            }
            else
            {
                scriptFunction = null;

                // 入口解析：`ClassName.Method` / `Namespace.ClassName.Method`（带点）或顶层/类静态函数名（无点）
                var entryLocation = new TextLocation(syntaxTrees[0].Text, new TextSpan(0, 0));
                if (entryPointName.IndexOf('.') >= 0)
                {
                    mainFunction = ResolveQualifiedEntryPoint(binder, entryPointName, entryLocation);
                }
                else
                {
                    var entryCandidates = functions.Where(f => f.Name == entryPointName).ToArray();
                    if (entryCandidates.Length == 1)
                    {
                        mainFunction = entryCandidates[0];
                    }
                    else if (entryCandidates.Length > 1)
                    {
                        // 顶层函数与类静态方法并存（或多个类同名静态方法）→ 歧义诊断，替代 SingleOrDefault 崩溃
                        binder.Diagnostics.ReportAmbiguousEntryPoint(entryLocation, entryPointName);
                        mainFunction = null;
                    }
                    else
                    {
                        mainFunction = null;
                    }
                }

                if (mainFunction != null)
                {
                    var returnTypeOk = mainFunction.ReturnType == TypeSymbol.Void || mainFunction.ReturnType == TypeSymbol.Int32;
                    var parametersOk = mainFunction.Parameters.Length == 0 ||
                                       (mainFunction.Parameters.Length == 1 && mainFunction.Parameters[0].Type == TypeSymbol.ArrayOf(TypeSymbol.String));
                    if (!parametersOk || !returnTypeOk)
                    {
                        binder.Diagnostics.ReportMainMustHaveCorrectSignature(mainFunction.Declaration!.Identifier.Location);
                    }
                }

                if (globalStatements.Any())
                {
                    if (mainFunction != null)
                    {
                        binder.Diagnostics.ReportCannotMixMainAndGlobalStatements(mainFunction.Declaration!.Identifier.Location);

                        foreach (var globalStatement in firstGlobalStatementPerSyntaxTree)
                        {
                            binder.Diagnostics.ReportCannotMixMainAndGlobalStatements(globalStatement.Location);
                        }
                    }
                    else
                    {
                        mainFunction = new FunctionSymbol(entryPointName, ImmutableArray<ParameterSymbol>.Empty, TypeSymbol.Void, null);
                    }
                }
            }

            var diagnostics = binder.Diagnostics.ToImmutableArray();
            var variables = binder._scope.GetDeclaredVariables();
            var enums = binder._scope.GetDeclaredEnums();
            var classes = binder._scope.GetDeclaredClasses();
            var usingNamespaces = binder._usingNamespaces.ToImmutableArray();
            var usingStatics = binder._usingStatics.ToImmutableArray();
            var usingAliases = binder._usingAliases.ToImmutableDictionary();

            if (previous != null)
            {
                diagnostics = diagnostics.InsertRange(0, previous.Diagnostics);
            }

            return new BoundGlobalScope(previous, diagnostics, mainFunction, scriptFunction, functions, enums, classes, variables, statements.ToImmutable(), usingNamespaces, usingStatics, usingAliases, (references ?? Array.Empty<string>()).ToImmutableArray());
        }

        public static BoundProgram BindProgram(bool isScript, BoundProgram? previous, BoundGlobalScope globalScope, ImmutableArray<CodProgram> codLibraries = default, Language? dialect = null, bool linkCodDynamically = false, NamespaceSymbol? globalNamespace = null)
        {
            dialect ??= Language.Cocoa;

            var parentScope = CreateParentScope(globalScope);
            InjectCodSymbols(parentScope, codLibraries);

            if (globalScope.Diagnostics.HasErrors())
            {
                return new BoundProgram(previous, globalScope.Diagnostics, null, null, ImmutableDictionary<FunctionSymbol, BoundBlockStatement>.Empty, globalScope.Classes);
            }

            var functionBodies = ImmutableDictionary.CreateBuilder<FunctionSymbol, BoundBlockStatement>();
            var genericOpenBodies = ImmutableDictionary.CreateBuilder<FunctionSymbol, BoundBlockStatement>();
            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

            foreach (var function in globalScope.Functions)
            {
                // 泛型定义方法（6e-M20）：模板体不进发射清单——实例化类方法经 Monomorphizer 重绑后并入。
                // 6e-G7 S2：改跳过为「构建开放绑定体」（T 保持开放的降级 Bound 块）随库携带，
                // 供消费方 BoundTreeSubstituter 替换展开（cod 库无源码，语法重绑路径不可达）。
                if (function.ContainingClass?.IsGenericDefinition == true)
                {
                    // 开放体在 T 开放上下文下绑定，个别转换检查会报「假阳性」诊断
                    // （历史边界：正因此前选语法重绑路线）——诊断不外泄，体照常携带供替换展开
                    if (!function.IsGenericMethod && !function.IsExtern && !function.IsAbstract && function.BuiltinKind == null)
                    {
                        var (openBody, _) = BuildFunctionBody(isScript, parentScope, function, globalScope, codLibraries, dialect, globalNamespace);
                        genericOpenBodies.Add(function, openBody);
                    }

                    continue;
                }

                if (function.IsGenericMethod)
                {
                    continue;
                }

                if (function.IsExtern)
                {
                    functionBodies.Add(function, new BoundBlockStatement(function.Declaration!, ImmutableArray<BoundStatement>.Empty));
                    continue;
                }

                if (function.IsAbstract || function.BuiltinKind != null)
                {
                    // 抽象成员（接口/抽象类）无实现：空 body；syscall 内部原语同样无实现
                    functionBodies.Add(function, new BoundBlockStatement(function.Declaration!, ImmutableArray<BoundStatement>.Empty));
                    continue;
                }

                var (loweredBody, bodyDiagnostics) = BuildFunctionBody(isScript, parentScope, function, globalScope, codLibraries, dialect, globalNamespace);
                functionBodies.Add(function, loweredBody);
                diagnostics.AddRange(bodyDiagnostics);
            }

            // 6e-M20 G2 单态化展开：语法扫描收集活实例化 → 实例化类方法体重绑（T→实参）→ 类并入发射清单
            // 6e-G7 S1：泛型定义单独携带（仅 .cod gcls 消费）
            var (emittedClasses, genericDefinitions) = Monomorphizer.Expand(globalScope, parentScope, isScript, codLibraries, dialect, functionBodies, diagnostics);

            // 6e-M22 C4：lambda 提升后处理——函数值携带的已绑定体入 Functions 清单
            // （BoundProgram.Functions == bodies 键集）；体中嵌套 lambda 一并发现，工作表至不动点
            {
                var pendingLambdaScopes = new Queue<BoundNode>();
                foreach (var statement in globalScope.Statements)
                {
                    pendingLambdaScopes.Enqueue(statement);
                }

                foreach (var existing in functionBodies.Keys)
                {
                    pendingLambdaScopes.Enqueue(functionBodies[existing]);
                }

                var environmentClasses = new HashSet<NamedTypeSymbol>();

                while (pendingLambdaScopes.Count > 0)
                {
                    foreach (var node in EnumerateBoundDescendants(pendingLambdaScopes.Dequeue()))
                    {
                        if (node is BoundFunctionValueExpression { Body: not null } functionValue &&
                            !functionBodies.ContainsKey(functionValue.Function))
                        {
                            functionBodies.Add(functionValue.Function, functionValue.Body);
                            pendingLambdaScopes.Enqueue(functionValue.Body);
                        }

                        // 6e-M22 C5：合成环境类并入发射清单（堆上对象承载捕获变量）
                        if (node is BoundFunctionValueExpression { EnvironmentClass: not null } withEnvironment)
                        {
                            environmentClasses.Add(withEnvironment.EnvironmentClass);
                        }
                    }
                }

                // 6e-M22 D-B：delegate 合成类不进发射（运行期表示 = Func/Action 对象，非自定义类）
                emittedClasses = emittedClasses
                    .Where(c => c.TypeKind != TypeKind.Delegate)
                    .Concat(environmentClasses)
                    .ToImmutableArray();
            }

            var compilationUnit = globalScope.Statements.Any()
                ? globalScope.Statements.First().Syntax.AncestorsAndSelf().LastOrDefault()
                : null;

            if (globalScope.MainFunction != null && globalScope.Statements.Any())
            {
                var body = Lowerer.Lower(globalScope.MainFunction, new BoundBlockStatement(compilationUnit!, globalScope.Statements));

                functionBodies.Add(globalScope.MainFunction, body);
            }
            else if (globalScope.ScriptFunction != null)
            {
                var statements = globalScope.Statements;

                if (statements.Length == 1 &&
                    statements[0] is BoundExpressionStatement es &&
                    es.Expression.Type != TypeSymbol.Void)
                {
                    statements = statements.SetItem(0, new BoundReturnStatement(es.Expression.Syntax, es.Expression));
                }
                else if (statements.Any() && statements.Last().Kind != BoundNodeKind.ReturnStatement)
                {
                    var nullValue = new BoundLiteralExpression(compilationUnit!, "");

                    statements = statements.Add(new BoundReturnStatement(compilationUnit!, nullValue));
                }

                var body = Lowerer.Lower(globalScope.ScriptFunction, new BoundBlockStatement(compilationUnit!, statements));

                functionBodies.Add(globalScope.ScriptFunction, body);
            }

            // `.cod` 库接入（语义层）：
            // 内联模式（默认/native/Evaluator）：库函数体合并进消费方 functionBodies，消费方同名优先；
            // 动态链接模式（dotnet 后端，阶段 A2）：不合并——符号经 AssemblyRef/MemberRef 指向各库 dll。
            // 两种模式下符号注入（BindGlobalScope→InjectCodSymbols）一致，绑定期无差别。
            var codAssemblies = ImmutableDictionary<object, string>.Empty;
            if (!codLibraries.IsDefaultOrEmpty)
            {
                if (!linkCodDynamically)
                {
                    foreach (var library in codLibraries)
                    {
                        foreach (var (fn, body) in library.Bodies)
                        {
                            // 6e-G7 S6：泛型定义方法（开放体）仅供 Monomorphizer 替换展开源使用，
                            // 不进消费方发射清单（开放类型无法编码到三后端）
                            if (fn.ContainingClass?.IsGenericDefinition == true || fn.IsGenericMethod)
                            {
                                continue;
                            }

                            if (!functionBodies.ContainsKey(fn))
                            {
                                functionBodies.Add(fn, body);
                            }
                        }
                    }
                }
                else
                {
                    // 溯源表：cod 符号 → 库程序集名。extern(P/Invoke) 本地声明；内建单例走消费方
                    // 本地运行时分发（BuiltinKind 分派），二者均不外链。
                    var provenance = ImmutableDictionary.CreateBuilder<object, string>(ReferenceEqualityComparer.Instance);
                    foreach (var library in codLibraries)
                    {
                        foreach (var fn in library.Functions)
                        {
                            if (fn.IsExtern || fn.BuiltinKind != null)
                            {
                                continue;
                            }

                            provenance[fn] = library.Name;
                            var containingClass = fn.ContainingClass;
                            if (containingClass != null && !SystemObjectMembers.IsBuiltinSystemClass(containingClass))
                            {
                                provenance[containingClass] = library.Name;
                            }
                        }

                        foreach (var containerClass in library.Classes)
                        {
                            if (!SystemObjectMembers.IsBuiltinSystemClass(containerClass))
                            {
                                provenance[containerClass] = library.Name;
                            }
                        }
                    }

                    codAssemblies = provenance.ToImmutable();
                }
            }

            return new BoundProgram(previous, diagnostics.ToImmutable(), globalScope.MainFunction, globalScope.ScriptFunction, functionBodies.ToImmutable(), emittedClasses, codAssemblies, genericDefinitions, genericOpenBodies.ToImmutable());
        }

        /// <summary>绑定树先序递归枚举（6e-M22 C4）：lambda 后处理走查用。</summary>
        private static IEnumerable<BoundNode> EnumerateBoundDescendants(BoundNode root)
        {
            yield return root;

            foreach (var child in Compilation.BoundChildren(root))
            {
                foreach (var descendant in EnumerateBoundDescendants(child))
                {
                    yield return descendant;
                }
            }
        }

        /// <summary>
        /// 单函数体构建（6e-M20 自 BindProgram 抽取复用）：方法体绑定 + 构造链/字段初始化器前缀 + 降级 + AllPathsReturn 检查。
        /// </summary>
        private static (BoundBlockStatement Body, ImmutableArray<Diagnostic> Diagnostics) BuildFunctionBody(bool isScript, BoundScope parentScope, FunctionSymbol function, BoundGlobalScope globalScope, ImmutableArray<CodProgram> codLibraries, Language dialect, NamespaceSymbol? globalNamespace)
        {
            var bodySyntax = function.Declaration?.Body;
            var bodyLocation = (SyntaxNode?)function.Declaration?.Identifier ?? function.Syntax;

            if (function.Syntax is ConstructorDeclarationSyntax ctorSyntax)
            {
                bodySyntax = ctorSyntax.Body;
                bodyLocation = (SyntaxNode?)ctorSyntax.ConstructorKeyword ?? ctorSyntax.OpenParenthesisToken;
            }

            var binder = new Binder(isScript, parentScope, function, globalScope.References, globalScope.UsingNamespaces, dialect, globalScope.UsingStatics, globalScope.UsingAliases, codLibraries, globalNamespace);
            if (function.Syntax is not LambdaExpressionSyntax)
            {
                // 6e-M22 C5：非 lambda 函数 = 环境宿主（其体内 lambda 的捕获变量由该环境对象承载）
                binder._environmentOwner = function;
            }
            BoundBlockStatement body;

            if (function.Syntax is PropertyAccessorSyntax accessorSyntax)
            {
                bodyLocation = accessorSyntax.Keyword;
                body = accessorSyntax.Body != null
                    ? (BoundBlockStatement)binder.BindStatement(accessorSyntax.Body)
                    : binder.BindAutoPropertyBody(accessorSyntax, function);
            }
            else if (bodySyntax == null)
            {
                // 无方法体（extern/abstract/隐式构造）：空 body
                body = new BoundBlockStatement(function.Syntax ?? function.Declaration!, ImmutableArray<BoundStatement>.Empty);
            }
            else
            {
                body = (BoundBlockStatement)binder.BindStatement(bodySyntax);
            }

            // 构造函数链：`base(...)` / `this(...)` → 函数体开头
            var prefixStatements = ImmutableArray<BoundStatement>.Empty;
            if (function.Syntax is ConstructorDeclarationSyntax chainCtor && chainCtor.InitializerKeyword != null)
            {
                var chain = binder.BindConstructorChain(chainCtor, function.ContainingClass!);
                if (chain != null)
                {
                    prefixStatements = ImmutableArray.Create<BoundStatement>(new BoundExpressionStatement(chainCtor, chain));
                }
            }
            // 隐式 base()：实例构造无显式链（含隐式默认构造），且基类有 0 参构造
            else if (function.IsConstructor && !function.IsStatic &&
                     function.ContainingClass != null && function.ContainingClass.BaseType != null)
            {
                var baseCtor = function.ContainingClass.BaseType.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Length == 0);
                if (baseCtor != null)
                {
                    var chain = new BoundConstructorChainExpression(function.Syntax ?? function.Declaration!, ConstructorInitializerKind.Base, baseCtor, ImmutableArray<BoundExpression>.Empty);
                    prefixStatements = ImmutableArray.Create<BoundStatement>(new BoundExpressionStatement(function.Syntax ?? function.Declaration!, chain));
                }
            }

            // 字段初始化器：实例字段 → 每个实例构造函数（构造链之后）；静态字段 → .cctor（body 即初始化语句）
            if (function.IsConstructor && function.ContainingClass != null)
            {
                var fieldInits = BindFieldInitializerStatements(binder, function.ContainingClass, function.IsStatic);
                if (fieldInits.Length > 0)
                {
                    prefixStatements = prefixStatements.AddRange(fieldInits);
                }
            }

            if (!prefixStatements.IsEmpty)
            {
                body = new BoundBlockStatement(bodySyntax ?? function.Syntax!, prefixStatements.AddRange(body.Statements));
            }

            var returnCheckLocation = function.ReturnType != TypeSymbol.Void && !function.IsAbstract
                ? (function.Declaration != null ? function.Declaration.Identifier.Location : bodyLocation.Location)
                : (TextLocation?)null;
            var loweredBody = LoweringPipeline.Lower(function, body, binder._diagnostics, returnCheckLocation);

            // 明确赋值分析（6e-M23 R4）：跟踪本函数 out 形参
            DefiniteAssignmentAnalysis.Analyze(
                loweredBody,
                function.Parameters.Where(p => p.IsOut).ToImmutableArray(),
                binder._diagnostics);

            return (loweredBody, binder.Diagnostics.ToImmutableArray());
        }

        /// <summary>
        /// Monomorphizer 专用：以实例化方法为容器重绑泛型定义语法。
        /// 与 BuildFunctionBody 同管道，另注入类型参数名→实参映射（T→int 等）。
        /// </summary>
        internal static (BoundBlockStatement Body, ImmutableArray<Diagnostic> Diagnostics) BuildFunctionBodyForMonomorphization(bool isScript, BoundScope parentScope, FunctionSymbol function, BoundGlobalScope globalScope, ImmutableArray<CodProgram> codLibraries, Language dialect, Dictionary<string, TypeSymbol> typeArgumentsByName)
        {
            var bodySyntax = function.Declaration?.Body;
            var bodyLocation = (SyntaxNode?)function.Declaration?.Identifier ?? function.Syntax;

            if (function.Syntax is ConstructorDeclarationSyntax ctorSyntax)
            {
                bodySyntax = ctorSyntax.Body;
                bodyLocation = (SyntaxNode?)ctorSyntax.ConstructorKeyword ?? ctorSyntax.OpenParenthesisToken;
            }

            var binder = new Binder(isScript, parentScope, function, globalScope.References, globalScope.UsingNamespaces, dialect, globalScope.UsingStatics, globalScope.UsingAliases, codLibraries);
            if (function.Syntax is not LambdaExpressionSyntax)
            {
                // 6e-M22 C5：非 lambda 函数 = 环境宿主（其体内 lambda 的捕获变量由该环境对象承载）
                binder._environmentOwner = function;
            }

            foreach (var (name, type) in typeArgumentsByName)
            {
                binder._typeArgumentsByName[name] = type;
            }

            BoundBlockStatement body;
            var prefixStatements = ImmutableArray<BoundStatement>.Empty;

            if (function.Syntax is PropertyAccessorSyntax accessorSyntax)
            {
                bodyLocation = accessorSyntax.Keyword;
                body = accessorSyntax.Body != null
                    ? (BoundBlockStatement)binder.BindStatement(accessorSyntax.Body)
                    : binder.BindAutoPropertyBody(accessorSyntax, function);
            }
            else if (bodySyntax == null)
            {
                // 隐式构造/无体方法：空体
                body = new BoundBlockStatement(function.Syntax ?? function.Declaration!, ImmutableArray<BoundStatement>.Empty);
            }
            else
            {
                body = (BoundBlockStatement)binder.BindStatement(bodySyntax);
            }

            // 构造链：`base(...)` / `this(...)` → 函数体开头
            if (function.Syntax is ConstructorDeclarationSyntax chainCtor && chainCtor.InitializerKeyword != null)
            {
                var chain = binder.BindConstructorChain(chainCtor, function.ContainingClass!);
                if (chain != null)
                {
                    prefixStatements = ImmutableArray.Create<BoundStatement>(new BoundExpressionStatement(chainCtor, chain));
                }
            }
            else if (function.IsConstructor && !function.IsStatic &&
                     function.ContainingClass != null && function.ContainingClass.BaseType != null)
            {
                var baseCtor = function.ContainingClass.BaseType.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Length == 0);
                if (baseCtor != null)
                {
                    var chain = new BoundConstructorChainExpression(function.Syntax ?? function.Declaration!, ConstructorInitializerKind.Base, baseCtor, ImmutableArray<BoundExpression>.Empty);
                    prefixStatements = ImmutableArray.Create<BoundStatement>(new BoundExpressionStatement(function.Syntax ?? function.Declaration!, chain));
                }
            }

            // 字段初始化器：实例字段 → 每个实例构造函数；静态字段 → .cctor
            if (function.IsConstructor && function.ContainingClass != null)
            {
                var fieldInits = BindFieldInitializerStatements(binder, function.ContainingClass, function.IsStatic);
                if (fieldInits.Length > 0)
                {
                    prefixStatements = prefixStatements.AddRange(fieldInits);
                }
            }

            if (!prefixStatements.IsEmpty)
            {
                body = new BoundBlockStatement(bodySyntax ?? function.Syntax!, prefixStatements.AddRange(body.Statements));
            }

            var returnCheckLocation = function.ReturnType != TypeSymbol.Void
                ? (TextLocation?)bodyLocation.Location
                : null;
            var loweredBody = LoweringPipeline.Lower(function, body, binder._diagnostics, returnCheckLocation);

            return (loweredBody, binder.Diagnostics.ToImmutableArray());
        }

    }
}
