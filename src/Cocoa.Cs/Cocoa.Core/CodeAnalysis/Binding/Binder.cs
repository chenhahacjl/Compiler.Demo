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
    internal sealed class Binder
    {
        private readonly DiagnosticBag _diagnostics = new DiagnosticBag();
        private readonly bool _isScript;
        private readonly FunctionSymbol? _function;
        private readonly ClassTypeSymbol? _currentClass;
        private readonly string[] _references;
        private readonly LanguageDialect _dialect;

        private readonly List<string> _usingNamespaces = new List<string>();
        private readonly List<string> _usingStatics = new List<string>();
        private readonly Dictionary<string, string> _usingAliases = new Dictionary<string, string>();
        private readonly ImmutableArray<CodProgram> _codLibraries;

        private Stack<(BoundLabel BreakLabel, BoundLabel ContinueLabel)> _loopStack = new Stack<(BoundLabel BreakLabel, BoundLabel ContinueLabel)>();
        private int _labelCounter;
        private BoundScope _scope;

        /// <summary>
        /// 类型实参名映射（6e-M20 单态化）：Monomorphizer 以实例化方法为容器重绑泛型定义语法时，
        /// 把类型参数名（T/U…）解析到具体实参。
        /// </summary>
        private readonly Dictionary<string, TypeSymbol> _typeArgumentsByName = new Dictionary<string, TypeSymbol>();

        /// <summary>类/接口声明绑定上下文（6e-M20）：成员签名/基类/约束解析期间的类型参数来源。</summary>
        private ClassTypeSymbol? _bindingClass;

        /// <summary>泛型方法签名绑定上下文（6e-M20）：BindFunctionDeclaration / 类方法 / 接口成员签名期间的 T 解析。</summary>
        private ImmutableArray<TypeParameterSymbol> _declaringMethodTypeParameters = ImmutableArray<TypeParameterSymbol>.Empty;

        /// <summary>lambda 提升全局序号（6e-M22 C4）：进程内单调，保证合成名 `__Lambda$N` 唯一。</summary>
        private static int _lambdaGlobalSequence;

        /// <summary>环境对象宿主函数（6e-M22 C5）：当前绑定上下文的捕获变量承载者；lambda 继承最外层非 lambda 函数。</summary>
        private FunctionSymbol? _environmentOwner;

        /// <summary>环境类缓存（6e-M22 C5）：每宿主函数一个合成 `__Env_&lt;fn&gt;` 类。</summary>
        private readonly Dictionary<FunctionSymbol, ClassTypeSymbol> _environmentClasses = new();

        /// <summary>lambda 体绑定深度（6e-M22 C5）：>0 时返回语句按推断语义处理（不套外层签名转换）。</summary>
        private int _lambdaBodyDepth;

        private bool IsBindingLambdaBody() => _lambdaBodyDepth > 0;

        /// <summary>设置声明绑定上下文（BindGlobalScope 阶段 3/3.2/3.5 调用）。</summary>
        internal void SetBindingClass(ClassTypeSymbol? classType) => _bindingClass = classType;

        internal Binder(bool isScript, BoundScope? parent, FunctionSymbol? function, ImmutableArray<string> references, ImmutableArray<string> usingNamespaces, LanguageDialect dialect, ImmutableArray<string> usingStatics = default, ImmutableDictionary<string, string> usingAliases = null, ImmutableArray<CodProgram> codLibraries = default)
        {
            _scope = new BoundScope(parent);
            _isScript = isScript;
            _function = function;
            _currentClass = function?.ContainingClass;
            _references = references.ToArray();
            _dialect = dialect;
            _codLibraries = codLibraries.IsDefault ? ImmutableArray<CodProgram>.Empty : codLibraries;
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
            parentScope.TryDeclareClass(ClassTypeSymbol.SystemDelegate);
            parentScope.TryDeclareClass(ClassTypeSymbol.SystemMulticastDelegate);

            var dialect = syntaxTrees.IsDefaultOrEmpty ? LanguageDialect.Cocoa : syntaxTrees[0].Dialect;
            var binder = new Binder(isScript, parentScope, null, references?.ToImmutableArray() ?? ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, dialect, codLibraries: codLibraries);

            binder.Diagnostics.AddRange(syntaxTrees.SelectMany(st => st.Diagnostics));
            if (binder.Diagnostics.HasErrors())
            {
                return new BoundGlobalScope(previous, binder.Diagnostics.ToImmutableArray(), null, null, ImmutableArray<FunctionSymbol>.Empty, ImmutableArray<EnumTypeSymbol>.Empty, ImmutableArray<ClassTypeSymbol>.Empty, ImmutableArray<VariableSymbol>.Empty, ImmutableArray<BoundStatement>.Empty, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, ImmutableDictionary<string, string>.Empty, (references ?? Array.Empty<string>()).ToImmutableArray());
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
            var classGroups = new List<(ClassTypeSymbol Type, List<(ClassDeclarationSyntax Syntax, string Namespace)> Parts)>();
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
            var interfaceSymbols = new List<(InterfaceDeclarationSyntax Syntax, string Namespace, ClassTypeSymbol Symbol)>();
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
                // 须先于成员绑定，override 签名解析/base 表达式/成员沿链上溯依赖基类链就位（接口不默认）
                if (!classType.IsInterface && classType.BaseType == null)
                {
                    classType.BaseType = ClassTypeSymbol.SystemObject;
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

        public static BoundProgram BindProgram(bool isScript, BoundProgram? previous, BoundGlobalScope globalScope, ImmutableArray<CodProgram> codLibraries = default, LanguageDialect dialect = LanguageDialect.Cocoa, bool linkCodDynamically = false)
        {
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
                        var (openBody, _) = BuildFunctionBody(isScript, parentScope, function, globalScope, codLibraries, dialect);
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

                var (loweredBody, bodyDiagnostics) = BuildFunctionBody(isScript, parentScope, function, globalScope, codLibraries, dialect);
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

                var environmentClasses = new HashSet<ClassTypeSymbol>();

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
                    .Where(c => !c.IsDelegateClass)
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
        private static (BoundBlockStatement Body, ImmutableArray<Diagnostic> Diagnostics) BuildFunctionBody(bool isScript, BoundScope parentScope, FunctionSymbol function, BoundGlobalScope globalScope, ImmutableArray<CodProgram> codLibraries, LanguageDialect dialect)
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

            var loweredBody = Lowerer.Lower(function, body);

            if (function.ReturnType != TypeSymbol.Void && !function.IsAbstract && !ControlFlowGraph.AllPathsReturn(loweredBody))
            {
                var location = function.Declaration != null
                    ? function.Declaration.Identifier.Location
                    : bodyLocation.Location;
                binder._diagnostics.ReportAllPathsMustReturn(location);
            }

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
        internal static (BoundBlockStatement Body, ImmutableArray<Diagnostic> Diagnostics) BuildFunctionBodyForMonomorphization(bool isScript, BoundScope parentScope, FunctionSymbol function, BoundGlobalScope globalScope, ImmutableArray<CodProgram> codLibraries, LanguageDialect dialect, Dictionary<string, TypeSymbol> typeArgumentsByName)
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

            var loweredBody = Lowerer.Lower(function, body);

            if (function.ReturnType != TypeSymbol.Void && !ControlFlowGraph.AllPathsReturn(loweredBody))
            {
                binder._diagnostics.ReportAllPathsMustReturn(bodyLocation.Location);
            }

            return (loweredBody, binder.Diagnostics.ToImmutableArray());
        }

        private void BindFunctionDeclaration(FunctionDeclarationSyntax syntax, string? namespaceName = null, string? importedDll = null)
        {
            // 泛型方法类型参数（6e-M20）先行落符号：签名 `(a: T, b: T): T` 的 T 解析依赖此上下文
            var previousMethodTypeParameters = _declaringMethodTypeParameters;
            _declaringMethodTypeParameters = BindFunctionTypeParameters(syntax.TypeParameters);

            try
            {
                var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();

                var seenParameterNames = new HashSet<string>();

                foreach (var parameterSyntax in syntax.Parameters)
                {
                    var parameterName = parameterSyntax.Identifier.Text;
                    var parameterType = BindTypeClause(parameterSyntax.Type);

                    if (!seenParameterNames.Add(parameterName))
                    {
                        _diagnostics.ReportParameterAlreadyDeclared(parameterSyntax.Location, parameterName);
                    }
                    else
                    {
                        var parameter = CreateParameterSymbol(parameterName, parameterType, parameterSyntax, parameters.Count);
                        parameters.Add(parameter);
                    }
                }

                var type = BindTypeClause(syntax.Type) ?? TypeSymbol.Void;

                var isExtern = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.CdeclKeyword || m.Kind == SyntaxKind.StdcallKeyword);
                var isSyscall = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.SyscallKeyword);

                if (isSyscall)
                {
                    _diagnostics.ReportSyscallFunctionTopLevel(syntax.Identifier.Location);
                }

                if (isExtern)
                {
                    // 6e-M17 Step 4：顶层位置式 extern 废弃 —— extern 必须声明在类的 import 块内
                    _diagnostics.ReportExternFunctionTopLevel(syntax.Identifier.Location);

                    if (syntax.Body != null)
                    {
                        _diagnostics.ReportExternFunctionCannotHaveBody(syntax.Body.Location);
                    }
                }

                var callingConvention = syntax.Modifiers.Select(m => m.Kind)
                    .FirstOrDefault(k => k == SyntaxKind.CdeclKeyword || k == SyntaxKind.StdcallKeyword) switch
                {
                    SyntaxKind.CdeclKeyword => CallingConvention.Cdecl,
                    SyntaxKind.StdcallKeyword => CallingConvention.StdCall,
                    _ => CallingConvention.Winapi,
                };

                var function = new FunctionSymbol(syntax.Identifier.Text, parameters.ToImmutable(), type, syntax, isExtern, importedDll, callingConvention, @namespace: namespaceName ?? "")
                {
                    TypeParameters = _declaringMethodTypeParameters,
                };
                BindWhereClauses(syntax.WhereClauses, function.TypeParameters);

                if (syntax.Identifier.Text != null && !_scope.TryDeclareFunction(function))
                {
                    _diagnostics.ReportSymbolAlreadyDeclared(syntax.Identifier.Location, function.Name);
                }

                // 命名空间函数同时注册进命名空间表（`Foo.Add(...)` 限定访问）；同名同签名由 TryDeclareFunction 已拦
                if (function.Namespace.Length > 0)
                {
                    _scope.TryDeclareNamespaceFunction(function.Namespace, function);
                }
            }
            finally
            {
                _declaringMethodTypeParameters = previousMethodTypeParameters;
            }
        }

        private ImmutableArray<ParameterSymbol> BindParameters(SeparatedSyntaxList<ParameterSyntax> parameterSyntaxList)
        {
            var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();

            var seenParameterNames = new HashSet<string>();

            foreach (var parameterSyntax in parameterSyntaxList)
            {
                var parameterName = parameterSyntax.Identifier.Text;
                var parameterType = BindTypeClause(parameterSyntax.Type);

                if (!seenParameterNames.Add(parameterName))
                {
                    _diagnostics.ReportParameterAlreadyDeclared(parameterSyntax.Location, parameterName);
                }
                else
                {
                    var parameter = CreateParameterSymbol(parameterName, parameterType, parameterSyntax, parameters.Count);
                    parameters.Add(parameter);
                }
            }

            return parameters.ToImmutable();
        }

        /// <summary>形参符号构造（6e-M23 R2）：携带 out/ref 修饰符；普通形参可赋值（对齐 C#），this 保持只读。</summary>
        private ParameterSymbol CreateParameterSymbol(string name, TypeSymbol type, ParameterSyntax syntax, int ordinal)
        {
            var isOut = syntax.Modifier?.Kind == SyntaxKind.OutKeyword;
            var isRef = syntax.Modifier?.Kind == SyntaxKind.RefKeyword;

            return new ParameterSymbol(name, type, ordinal, isOut, isRef);
        }

        /// <summary>泛型方法类型参数绑定（6e-M20）：建 TypeParameterSymbol 列表（重名/与类类型参数同名诊断）。</summary>
        private ImmutableArray<TypeParameterSymbol> BindFunctionTypeParameters(TypeParameterListSyntax? syntax)
        {
            if (syntax == null)
            {
                return ImmutableArray<TypeParameterSymbol>.Empty;
            }

            var parameters = ImmutableArray.CreateBuilder<TypeParameterSymbol>();
            var seen = new HashSet<string>();

            // 类类型参数先入集：方法级同名遮蔽报错（对齐 C# CS0693 提示语义）
            foreach (var outer in _bindingClass?.TypeParameters ?? _currentClass?.TypeParameters ?? ImmutableArray<TypeParameterSymbol>.Empty)
            {
                seen.Add(outer.Name);
            }

            foreach (var parameterToken in syntax.Parameters)
            {
                var parameterName = parameterToken.Text ?? "";
                if (parameterName.Length == 0)
                {
                    continue;
                }

                if (!seen.Add(parameterName))
                {
                    _diagnostics.ReportError(parameterToken.Location, $"类型参数 '{parameterName}' 重复或与外层类型参数同名。");
                    continue;
                }

                parameters.Add(new TypeParameterSymbol(parameterName, parameters.Count, owningClass: null));
            }

            return parameters.ToImmutable();
        }

        /// <summary>从修饰符列表解析可见性（public &gt; internal &gt; protected &gt; private；无修饰符取默认值）。</summary>
        private static Visibility GetVisibility(ImmutableArray<SyntaxToken> modifiers, Visibility defaultVisibility)
        {
            if (modifiers.Any(m => m.Kind == SyntaxKind.PublicKeyword))
            {
                return Visibility.Public;
            }

            if (modifiers.Any(m => m.Kind == SyntaxKind.InternalKeyword))
            {
                return Visibility.Internal;
            }

            if (modifiers.Any(m => m.Kind == SyntaxKind.ProtectedKeyword))
            {
                return Visibility.Protected;
            }

            if (modifiers.Any(m => m.Kind == SyntaxKind.PrivateKeyword))
            {
                return Visibility.Private;
            }

            return defaultVisibility;
        }

        private static bool IsVisibilityModifier(SyntaxKind kind)
        {
            return kind == SyntaxKind.PublicKeyword ||
                   kind == SyntaxKind.InternalKeyword ||
                   kind == SyntaxKind.ProtectedKeyword ||
                   kind == SyntaxKind.PrivateKeyword;
        }

        private static bool HasVisibilityModifier(ImmutableArray<SyntaxToken> modifiers)
        {
            return modifiers.Any(m => IsVisibilityModifier(m.Kind));
        }

        /// <summary>
        /// 访问器可见性校验（严格对齐 C#）：① 访问器带可见性修饰符时必须严格更受限（CS0273，相等也报错）；
        /// ② get/set 至多一个可带可见性修饰符。可见性序：Public(0) &lt; Internal(1) &lt; Protected(2) &lt; Private(3)，数值越大越受限。
        /// </summary>
        private void ValidateAccessorVisibility(PropertyDeclarationSyntax syntax, Visibility propertyVisibility)
        {
            var hasGetModifier = syntax.Getter != null && HasVisibilityModifier(syntax.Getter.Modifiers);
            var hasSetModifier = syntax.Setter != null && HasVisibilityModifier(syntax.Setter.Modifiers);

            if (hasGetModifier && hasSetModifier)
            {
                var location = (syntax.Setter?.Keyword ?? syntax.Getter?.Keyword).Location;
                _diagnostics.ReportAccessorModifierOnBothAccessors(location, syntax.Identifier.Text);
            }

            if (hasGetModifier && syntax.Getter != null &&
                GetVisibility(syntax.Getter.Modifiers, propertyVisibility) <= propertyVisibility)
            {
                _diagnostics.ReportAccessorVisibilityNotMoreRestrictive(syntax.Getter.Keyword.Location, syntax.Identifier.Text);
            }

            if (hasSetModifier && syntax.Setter != null &&
                GetVisibility(syntax.Setter.Modifiers, propertyVisibility) <= propertyVisibility)
            {
                _diagnostics.ReportAccessorVisibilityNotMoreRestrictive(syntax.Setter.Keyword.Location, syntax.Identifier.Text);
            }
        }

        /// <summary>成员可见性判定（private 仅含类；protected 含类及派生类；internal 同程序集恒可访问）。</summary>
        private bool IsAccessibleMember(Visibility visibility, ClassTypeSymbol containingClass)
        {
            switch (visibility)
            {
                case Visibility.Public:
                case Visibility.Internal:
                    return true;
                case Visibility.Protected:
                    return _currentClass != null && (containingClass == _currentClass || containingClass.IsBaseOf(_currentClass));
                case Visibility.Private:
                default:
                    return _currentClass != null && containingClass == _currentClass;
            }
        }

        /// <summary>创建类符号；部分类（partial）的多段声明合并为同一符号（各段成员分别绑定）。</summary>
        private ClassTypeSymbol DeclareClassGroup(List<(ClassDeclarationSyntax Syntax, string Namespace)> parts)
        {
            var primary = parts[0];
            var name = primary.Syntax.Identifier.Text;
            var visibility = GetVisibility(primary.Syntax.Modifiers, Visibility.Internal);

            // `facade` 修饰符（6e-M20）：仅类有意义；须命中 FacadeTargets 才被认领为基元成员面载体
            if (primary.Syntax.Modifiers.Any(m => m.Kind == SyntaxKind.FacadeKeyword) &&
                !FacadeTargets.ContainsKey(primary.Namespace.Length == 0 ? name : primary.Namespace + "." + name))
            {
                _diagnostics.ReportInvalidFacadeMarker(
                    primary.Syntax.Identifier.Location,
                    primary.Namespace.Length == 0 ? name : primary.Namespace + "." + name);
            }

            if (parts.Count > 1)
            {
                for (var i = 1; i < parts.Count; i++)
                {
                    var part = parts[i];

                    if (!part.Syntax.Modifiers.Any(m => m.Kind == SyntaxKind.PartialKeyword))
                    {
                        _diagnostics.ReportSymbolAlreadyDeclared(part.Syntax.Identifier.Location, name);
                    }

                    var partVisibility = GetVisibility(part.Syntax.Modifiers, Visibility.Internal);
                    if (partVisibility != visibility)
                    {
                        _diagnostics.ReportError(part.Syntax.Identifier.Location, $"部分类 '{name}' 的多个部分可见性不一致。");
                    }
                }
            }

            foreach (var (syntax, ns) in parts)
            {
                if (GetVisibility(syntax.Modifiers, Visibility.Internal) is Visibility.Private or Visibility.Protected)
                {
                    _diagnostics.ReportError(syntax.Identifier.Location, $"类 '{name}' 的可见性只能为 public 或 internal。");
                }
            }

            // 6e-M26：struct（值类型）→ StructTypeSymbol；class → ClassTypeSymbol
            var isStruct = primary.Syntax.IsStruct;
            ClassTypeSymbol classType = isStruct
                ? new StructTypeSymbol(name, primary.Namespace, visibility, primary.Syntax)
                : new ClassTypeSymbol(name, primary.Namespace, visibility, primary.Syntax);
            classType.IsAbstract = parts.Any(p => p.Syntax.Modifiers.Any(m => m.Kind == SyntaxKind.AbstractKeyword));
            classType.IsSealed = parts.Any(p => p.Syntax.Modifiers.Any(m => m.Kind == SyntaxKind.SealedKeyword));

            // struct 约束（MVP）：不可有基类/接口、不可 abstract/facade、不可 partial（v1）
            if (isStruct)
            {
                foreach (var (syntax, _) in parts)
                {
                    if (syntax.BaseTypes.Length > 0)
                    {
                        _diagnostics.ReportError(syntax.Identifier.Location, $"struct '{name}' 不能有基类或实现接口（MVP 阶段仅支持值字段/构造器）。");
                    }

                    if (syntax.Modifiers.Any(m => m.Kind == SyntaxKind.AbstractKeyword))
                    {
                        _diagnostics.ReportError(syntax.Identifier.Location, $"struct '{name}' 不能声明为 abstract。");
                    }

                    if (syntax.Modifiers.Any(m => m.Kind == SyntaxKind.FacadeKeyword))
                    {
                        _diagnostics.ReportError(syntax.Identifier.Location, $"struct '{name}' 不能声明为 facade。");
                    }
                }
            }

            // 泛型类型参数声明（6e-M20）：`class Box<T, U>`——部分类各段须一致
            var typeParameters = BindClassTypeParameters(primary.Syntax.TypeParameters, classType, name);
            foreach (var (syntax, _) in parts.Skip(1))
            {
                if (!SyntaxTypeParametersMatch(syntax.TypeParameters, typeParameters))
                {
                    _diagnostics.ReportError(syntax.Identifier.Location, $"部分类 '{name}' 的多个部分的类型参数列表不一致。");
                }
            }

            classType.TypeParameters = typeParameters;

            // where 约束解析在阶段 3.2（接口全部声明后）——约束可引用后置接口
            if (!_scope.TryDeclareClass(classType))
            {
                _diagnostics.ReportSymbolAlreadyDeclared(primary.Syntax.Identifier.Location, name);
            }

            return classType;
        }

        /// <summary>阶段 3.2：类泛型 where 约束解析（6e-M20；接口/类符号均已就位）。</summary>
        private void BindClassWhereClauses(List<(ClassDeclarationSyntax Syntax, string Namespace)> parts, ClassTypeSymbol classType)
        {
            var previous = _bindingClass;
            _bindingClass = classType;

            try
            {
                BindWhereClauses(parts.SelectMany(p => p.Syntax.WhereClauses), classType.TypeParameters);
            }
            finally
            {
                _bindingClass = previous;
            }
        }

        /// <summary>类泛型类型参数绑定：建 TypeParameterSymbol 列表（重名/与类名冲突诊断）。</summary>
        private ImmutableArray<TypeParameterSymbol> BindClassTypeParameters(TypeParameterListSyntax? syntax, ClassTypeSymbol classType, string className)
        {
            if (syntax == null)
            {
                return ImmutableArray<TypeParameterSymbol>.Empty;
            }

            var parameters = ImmutableArray.CreateBuilder<TypeParameterSymbol>();
            var seen = new HashSet<string>();

            foreach (var parameterToken in syntax.Parameters)
            {
                var parameterName = parameterToken.Text ?? "";
                if (parameterName.Length == 0)
                {
                    continue;
                }

                if (!seen.Add(parameterName))
                {
                    _diagnostics.ReportError(parameterToken.Location, $"类型参数 '{parameterName}' 重复。");
                    continue;
                }

                parameters.Add(new TypeParameterSymbol(parameterName, parameters.Count, classType));
            }

            if (parameters.Any(p => p.Name == className))
            {
                _diagnostics.ReportError(syntax.Location, $"类型参数不能与类 '{className}' 同名。");
            }

            return parameters.ToImmutable();
        }

        /// <summary>部分类各段类型参数列表一致性（按名字逐一比较）。</summary>
        private static bool SyntaxTypeParametersMatch(TypeParameterListSyntax? syntax, ImmutableArray<TypeParameterSymbol> expected)
        {
            if (syntax == null)
            {
                return expected.IsEmpty;
            }

            if (syntax.Parameters.Length != expected.Length)
            {
                return false;
            }

            for (var i = 0; i < syntax.Parameters.Length; i++)
            {
                if (syntax.Parameters[i].Text != expected[i].Name)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// where 约束子句解析（6e-M20）：约束类型经 LookupType 解析（可为接口/基类/其他类型参数）；
        /// `new()` / `class` 走标志位。未知类型参数名报错。实例化期校验实参满足约束。
        /// </summary>
        private void BindWhereClauses(IEnumerable<WhereClauseSyntax> clauses, ImmutableArray<TypeParameterSymbol> typeParameters)
        {
            foreach (var clause in clauses)
            {
                var parameterName = clause.Identifier.Text;
                var target = typeParameters.FirstOrDefault(p => p.Name == parameterName);

                if (target == null)
                {
                    _diagnostics.ReportError(clause.Identifier.Location, $"'{parameterName}' 不是本声明的类型参数。");
                    continue;
                }

                var constraints = ImmutableArray.CreateBuilder<TypeSymbol>();
                foreach (var constraintSyntax in clause.ConstraintTypes)
                {
                    var text = constraintSyntax.Identifier.Text;
                    if (text == "new()")
                    {
                        target.HasNewConstraint = true;
                        continue;
                    }

                    if (text == "class")
                    {
                        if (target.HasValueTypeConstraint)
                        {
                            _diagnostics.ReportError(constraintSyntax.Location, $"类型参数 '{parameterName}' 不能同时具有 'struct' 与 'class' 约束。");
                            continue;
                        }

                        target.HasReferenceTypeConstraint = true;
                        continue;
                    }

                    // struct 值类型约束（6e-M22 C1）：非关键字，按约束文本特判（与 C# 一致保留字面）
                    if (text == "struct")
                    {
                        if (target.HasReferenceTypeConstraint)
                        {
                            _diagnostics.ReportError(constraintSyntax.Location, $"类型参数 '{parameterName}' 不能同时具有 'class' 与 'struct' 约束。");
                            continue;
                        }

                        target.HasValueTypeConstraint = true;
                        continue;
                    }

                    var constraintType = BindTypeClause(constraintSyntax);
                    if (constraintType == null)
                    {
                        continue;
                    }

                    constraints.Add(constraintType);
                }

                target.ConstraintTypes = target.ConstraintTypes.AddRange(constraints);
            }
        }

        private void BindClassBase(ClassDeclarationSyntax syntax, ClassTypeSymbol classType)
        {
            // 6e-M20：声明上下文（泛型基类 `class MyList<T> extends List<T>` 的 T 解析）
            var previousBindingClass = _bindingClass;
            _bindingClass = classType;

            try
            {
                BindClassBaseCore(syntax, classType);
            }
            finally
            {
                _bindingClass = previousBindingClass;
            }
        }

        private void BindClassBaseCore(ClassDeclarationSyntax syntax, ClassTypeSymbol classType)
        {
            // 基类型解析（`class Foo: Bar, IA, IB`；首个非接口 = 基类，其余须为接口；部分类多段声明时基类必须一致）
            // 6e-M20：泛型基类/基接口经实参实例化
            var seenNonInterface = false;
            foreach (var baseClause in syntax.BaseTypes)
            {
                var baseName = baseClause.Identifier.Text;
                var baseType = BindBaseTypeClause(baseClause);

                if (baseType == null)
                {
                    continue;
                }
                else if (baseType.IsInterface)
                {
                    // 类实现接口：`class Rectangle: IShape`
                    classType.AddInterface(baseType);
                }
                else
                {
                    // 非接口基类：至多一个
                    if (seenNonInterface)
                    {
                        _diagnostics.ReportError(baseClause.Location, $"类 '{classType.Name}' 只能有一个非接口基类。");
                    }
                    else if (classType.BaseType != null)
                    {
                        if (classType.BaseType != baseType)
                        {
                            _diagnostics.ReportError(syntax.Identifier.Location, $"部分类 '{classType.Name}' 的多个部分声明的基类不一致。");
                        }
                    }
                    else if (baseType.IsSealed)
                    {
                        _diagnostics.ReportCannotInheritSealed(syntax.Identifier.Location, baseName);
                    }
                    else
                    {
                        classType.BaseType = baseType;

                        // 循环继承检测：沿基类链查找本类
                        var seen = new HashSet<ClassTypeSymbol>();
                        var circular = false;
                        for (var current = baseType; current != null && seen.Add(current); current = current.BaseType)
                        {
                            if (current == classType)
                            {
                                circular = true;
                                break;
                            }
                        }

                        if (circular)
                        {
                            _diagnostics.ReportCircularInheritance(syntax.Identifier.Location, baseName);
                            classType.BaseType = null;
                        }
                    }

                    seenNonInterface = true;
                }
            }
        }

        /// <summary>
        /// 是否有可用基类（6e-M19 M2-c 反转）：内建 System.Object 携带真实成员面（虚四方法），
        /// 视为真基类——override 解析、base 表达式、成员沿链上溯均正常工作。
        /// 仅接口（BaseType=null）无基类。
        /// </summary>
        private static bool HasBaseClass(ClassTypeSymbol classType)
            => classType.BaseType != null;

        /// <summary>
        /// 基类/基接口子句绑定（6e-M20 泛型感知）：`extends List&lt;T&gt;` / `: Collection&lt;int&gt;`
        /// 经泛型名解析实例化；裸泛型定义报错并返回 null。
        /// </summary>
        private ClassTypeSymbol? BindBaseTypeClause(TypeClauseSyntax syntax)
        {
            TypeSymbol? resolved;

            if (syntax is GenericTypeClauseSyntax generic)
            {
                resolved = BindGenericTypeClause(generic);
            }
            else
            {
                var lookup = LookupType(syntax.Identifier.Text);
                if (lookup is ClassTypeSymbol { IsGenericDefinition: true } nakedGeneric)
                {
                    _diagnostics.ReportGenericDefinitionRequiresTypeArguments(syntax.Identifier.Location, nakedGeneric.Name);
                    return null;
                }

                resolved = lookup;
            }

            return resolved as ClassTypeSymbol;
        }

        private void BindClassMembers(ClassDeclarationSyntax syntax, ClassTypeSymbol classType, List<FunctionSymbol> classFunctions, string @namespace)
        {
            // 6e-M20：声明上下文（字段/方法签名的 T 解析）
            var previousBindingClass = _bindingClass;
            _bindingClass = classType;

            try
            {
                BindClassMembersCore(syntax, classType, classFunctions, @namespace);
            }
            finally
            {
                _bindingClass = previousBindingClass;
            }
        }

        private void BindClassMembersCore(ClassDeclarationSyntax syntax, ClassTypeSymbol classType, List<FunctionSymbol> classFunctions, string @namespace)
        {
            foreach (var member in syntax.Members)
            {
                if (member.Modifiers.Any(m => m.Kind == SyntaxKind.PartialKeyword))
                {
                    _diagnostics.ReportError(member.Location, "partial 只能用于类声明。");
                    continue;
                }

                if (classType.IsStatic &&
                    (member is ClassFieldDeclarationSyntax && !member.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword) ||
                     member is FunctionDeclarationSyntax && !member.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword)))
                {
                    _diagnostics.ReportError(member.Location, $"静态类 {classType.Name} 只能包含静态成员。");
                }

                if (member is ClassFieldDeclarationSyntax fieldDeclaration)
                {
                    var fieldType = BindTypeClause(fieldDeclaration.Type);
                    var fieldVisibility = GetVisibility(fieldDeclaration.Modifiers, Visibility.Private);
                    var fieldIsReadonly = fieldDeclaration.Modifiers.Any(m => m.Kind == SyntaxKind.ReadonlyKeyword);
                    var fieldIsStatic = fieldDeclaration.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword);

                    if (classType.GetDeclaredField(fieldDeclaration.Identifier.Text) == null)
                    {
                        classType.AddField(new FieldSymbol(fieldDeclaration.Identifier.Text, fieldType, fieldVisibility, classType, isReadonly: fieldIsReadonly, isStatic: fieldIsStatic));
                    }
                    else
                    {
                        _diagnostics.ReportSymbolAlreadyDeclared(fieldDeclaration.Identifier.Location, fieldDeclaration.Identifier.Text);
                    }
                }
                else if (member is ConstructorDeclarationSyntax constructorDeclaration)
                {
                    var isStatic = constructorDeclaration.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword);

                    if (isStatic)
                    {
                        // 静态构造函数（C# 式 `static Foo()` / Cocoa 式 `static constructor()`）→ `.cctor` 符号
                        var location = constructorDeclaration.ConstructorKeyword != null
                            ? constructorDeclaration.ConstructorKeyword.Location
                            : constructorDeclaration.OpenParenthesisToken.Location;

                        if (HasVisibilityModifier(constructorDeclaration.Modifiers))
                        {
                            _diagnostics.ReportError(location, "静态构造函数不能有可见性修饰符（public/private/internal/protected）。");
                        }

                        if (constructorDeclaration.Parameters.Count > 0)
                        {
                            _diagnostics.ReportError(constructorDeclaration.OpenParenthesisToken.Location, "静态构造函数不能有参数。");
                        }

                        if (constructorDeclaration.InitializerKeyword != null)
                        {
                            _diagnostics.ReportError(constructorDeclaration.InitializerKeyword.Location, "静态构造函数不能有构造链（base/this）。");
                        }

                        if (classType.GetDeclaredMethod(".cctor") == null)
                        {
                            var cctor = new FunctionSymbol(".cctor", ImmutableArray<ParameterSymbol>.Empty, TypeSymbol.Void, null,
                                syntax: constructorDeclaration, containingClass: classType, visibility: Visibility.Private) { IsConstructor = true, IsStatic = true };
                            classType.AddMethod(cctor);
                            classFunctions.Add(cctor);
                        }
                        else
                        {
                            _diagnostics.ReportSymbolAlreadyDeclared(location, ".cctor");
                        }
                    }
                    else
                    {
                        var parameters = BindParameters(constructorDeclaration.Parameters);
                        var ctorVisibility = GetVisibility(constructorDeclaration.Modifiers, Visibility.Private);

                        if (classType.GetDeclaredMethod(classType.Name) == null)
                        {
                            var ctor = new FunctionSymbol(classType.Name, parameters, TypeSymbol.Void, null, syntax: constructorDeclaration, containingClass: classType, visibility: ctorVisibility) { IsConstructor = true };
                            classType.AddMethod(ctor);
                            classFunctions.Add(ctor);
                        }
                        else
                        {
                            var location = constructorDeclaration.ConstructorKeyword != null
                                ? constructorDeclaration.ConstructorKeyword.Location
                                : constructorDeclaration.OpenParenthesisToken.Location;
                            _diagnostics.ReportSymbolAlreadyDeclared(location, classType.Name);
                        }
                    }
                }
                else if (member is FunctionDeclarationSyntax methodDeclaration)
                {
                    var method = BindClassMethodDeclaration(methodDeclaration, classType, dllName: null);

                    if (!classType.HasDeclaredMethodSignature(methodDeclaration.Identifier.Text, method))
                    {
                        classType.AddMethod(method);
                        classFunctions.Add(method);
                    }
                    else
                    {
                        _diagnostics.ReportSymbolAlreadyDeclared(methodDeclaration.Identifier.Location, methodDeclaration.Identifier.Text);
                    }
                }
                else if (member is ImportBlockSyntax importBlock)
                {
                    BindImportBlock(importBlock, classType, classFunctions);
                }
                else if (member is PropertyDeclarationSyntax propertyDeclaration)
                {
                    BindPropertyDeclaration(propertyDeclaration, classType, classFunctions);
                }
                else if (member is EventDeclarationSyntax eventDeclaration)
                {
                    BindEventDeclaration(eventDeclaration, classType);
                }
                else if (member is DelegateDeclarationSyntax delegateDeclaration)
                {
                    BindDelegateDeclaration(delegateDeclaration, classType, classFunctions);
                }
            }
        }

        /// <summary>
        /// 事件声明绑定（6e-M22 C5+ 多播）：解析处理器类型为 FunctionTypeSymbol → 创建 EventSymbol 挂到类，
        /// 合成隐藏后备字段 `_<eventName>`（类型 = 处理器签名的数组，初值 null）。
        /// 订阅/触发的多播语义在语句级脱糖（TryBindEventSubscription / BindEventRaise），三后端零改动。
        /// </summary>
        private void BindEventDeclaration(EventDeclarationSyntax syntax, ClassTypeSymbol classType)
        {
            var handlerType = BindTypeClause(syntax.HandlerType);
            if (handlerType == null)
                return;

            // 6e-M22 D-B：delegate 类处理器 → 提取 Invoke 签名作为 FunctionTypeSymbol
            FunctionTypeSymbol resolvedHandler;
            if (handlerType is FunctionTypeSymbol fts)
            {
                resolvedHandler = fts;
            }
            else if (handlerType is ClassTypeSymbol { IsDelegateClass: true } dc)
            {
                var sig = dc.GetDelegateSignature();
                if (sig == null)
                {
                    _diagnostics.ReportError(syntax.Identifier.Location, $"delegate 类 '{dc.Name}' 缺少 Invoke 方法。");
                    return;
                }

                resolvedHandler = sig;
            }
            else
            {
                _diagnostics.ReportError(syntax.Identifier.Location, $"事件处理器类型 '{handlerType.Name}' 不是函数类型或 delegate。");
                return;
            }

            var eventName = syntax.Identifier.Text;

            // 静态事件后置（设计 §7.3）：当前多播存储为实例字段，明确拒绝
            if (syntax.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword))
            {
                _diagnostics.ReportStaticEventNotSupported(syntax.Identifier.Location, eventName);
                return;
            }

            var visibility = GetVisibility(syntax.Modifiers, Visibility.Public);
            var eventSymbol = new EventSymbol(eventName, resolvedHandler, visibility, classType);
            classType.AddEvent(eventSymbol);

            // 多播存储：函数值数组字段（初值 null；+= 尾插 / -= 引用相等移除首匹配 / 触发判空快照遍历）
            classType.AddField(new FieldSymbol("_" + eventName, TypeSymbol.ArrayOf(resolvedHandler), visibility, classType));
        }

        /// <summary>
        /// 事件订阅脱糖（6e-M22 C5+ 多播）：`e += f` / `e -= f` → 语句块。
        /// += 尾插（null → 单元素数组；否则复制扩容）；-= 按引用相等移除首个匹配（清空后回置 null）。
        /// 处理器表达式只求值一次（提升隐藏局部）。返回 null 表示目标不是事件（走通用绑定）。
        /// </summary>
        private BoundStatement? TryBindEventSubscription(AssignmentExpressionSyntax syntax)
        {
            var operatorKind = syntax.AssignmentToken.Kind;
            if (operatorKind != SyntaxKind.PlusEqualsToken && operatorKind != SyntaxKind.MinusEqualsToken)
            {
                return null;
            }

            // 目标形态：`obj.e` / `this.e` / 类内裸名 `e`
            string? eventName = null;
            ClassTypeSymbol? ownerClass = null;
            BoundExpression? receiver = null;

            if (syntax.Target.Kind == SyntaxKind.MemberAccessExpression)
            {
                var memberAccess = (MemberAccessExpressionSyntax)syntax.Target;
                var boundReceiver = BindExpression(memberAccess.Expression);

                if (boundReceiver.Type is ClassTypeSymbol candidate &&
                    candidate.GetEvent(memberAccess.IdentifierToken.Text) is EventSymbol)
                {
                    receiver = boundReceiver;
                    eventName = memberAccess.IdentifierToken.Text;
                    ownerClass = candidate;
                }
            }
            else if (syntax.Target.Kind == SyntaxKind.NameExpression && _currentClass != null)
            {
                var nameIdentifier = ((NameExpressionSyntax)syntax.Target).IdentifierToken.Text;

                if (_currentClass.GetEvent(nameIdentifier) is EventSymbol)
                {
                    receiver = new BoundThisExpression(syntax.Target, _currentClass);
                    eventName = nameIdentifier;
                    ownerClass = _currentClass;
                }
            }

            if (ownerClass == null || receiver == null || eventName == null)
            {
                return null;
            }

            var eventSymbol = ownerClass.GetEvent(eventName)!;

            if (!IsAccessibleMember(eventSymbol.Visibility, ownerClass))
            {
                _diagnostics.ReportCannotAccessMember(syntax.AssignmentToken.Location, eventName, eventSymbol.Visibility);
                return new BoundBlockStatement(syntax, ImmutableArray<BoundStatement>.Empty);
            }

            var signature = eventSymbol.HandlerType;
            var backingField = ownerClass.GetField("_" + eventName)!;
            var handlerArray = TypeSymbol.ArrayOf(signature);

            _labelCounter++;
            var sequence = _labelCounter;
            var handlerLocal = new LocalVariableSymbol($"__evt{sequence}_h", isReadOnly: true, signature, null);
            var oldListLocal = new LocalVariableSymbol($"__evt{sequence}_old", isReadOnly: true, handlerArray, null);

            var fieldAccess = new BoundMemberAccessExpression(syntax, handlerArray, receiver, backingField.Name, backingField);

            // 处理器绑定：先常规绑定（裸函数名已是函数值），再归一化类型——
            // delegate 类变量/表达式提取 Invoke 签名核对；不匹配时回退语法级转换（方法组/期望类型下推）。
            var boundHandler = BindExpression(syntax.Expression);
            var handlerType = boundHandler.Type switch
            {
                ClassTypeSymbol { IsDelegateClass: true } delegateClass => delegateClass.GetDelegateSignature(),
                var other => other,
            };

            if (handlerType != TypeSymbol.Error && handlerType != signature)
            {
                boundHandler = BindConversion(syntax.Expression, signature);
            }

            var statements = ImmutableArray.CreateBuilder<BoundStatement>();
            statements.Add(new BoundVariableDeclaration(syntax, handlerLocal, boundHandler));
            statements.Add(new BoundVariableDeclaration(syntax, oldListLocal, fieldAccess));

            var nullLiteral = new BoundLiteralExpression(syntax, null, TypeSymbol.Null);

            if (operatorKind == SyntaxKind.PlusEqualsToken)
            {
                // += 尾插：
                // if __old == null { _<e> = new Fn[1] { __h } }
                // else {
                //     __n = new Fn[__old.Length + 1]
                //     while __i < __old.Length { __n[__i] = __old[__i]; __i++ }
                //     __n[__old.Length] = __h
                //     _<e> = __n
                // }
                var isNullCondition = BoundNodeFactory.Binary(syntax,
                    BoundNodeFactory.Variable(syntax, oldListLocal),
                    SyntaxKind.EqualsEqualsToken,
                    nullLiteral);

                var singleItem = new BoundArrayCreationExpression(
                    syntax, handlerArray,
                    BoundNodeFactory.Literal(syntax, 1),
                    ImmutableArray.Create<BoundExpression>(BoundNodeFactory.Variable(syntax, handlerLocal)));
                var storeSingle = new BoundExpressionStatement(
                    syntax,
                    new BoundMemberAssignmentExpression(syntax, receiver, backingField, singleItem));

                var growStatements = new List<BoundStatement>();
                var newListLocal = new LocalVariableSymbol($"__evt{sequence}_new", isReadOnly: false, handlerArray, null);
                var indexLocal = new LocalVariableSymbol($"__evt{sequence}_i", isReadOnly: false, TypeSymbol.Int32, null);

                growStatements.Add(new BoundVariableDeclaration(
                    syntax, newListLocal,
                    new BoundArrayCreationExpression(
                        syntax, handlerArray,
                        BoundNodeFactory.Add(syntax,
                            LengthOf(syntax, oldListLocal),
                            BoundNodeFactory.Literal(syntax, 1)),
                        ImmutableArray<BoundExpression>.Empty)));
                growStatements.Add(new BoundVariableDeclaration(syntax, indexLocal, BoundNodeFactory.Literal(syntax, 0)));

                var copyLoop = BuildElementCopyLoop(syntax, newListLocal, indexLocal, oldListLocal, $"__evt{sequence}_br");
                foreach (var statement in copyLoop)
                {
                    growStatements.Add(statement);
                }

                growStatements.Add(new BoundExpressionStatement(syntax, new BoundElementAssignmentExpression(
                    syntax, signature,
                    ElementOf(syntax, newListLocal, LengthOf(syntax, oldListLocal)),
                    BoundNodeFactory.Variable(syntax, handlerLocal))));

                growStatements.Add(new BoundExpressionStatement(syntax, new BoundMemberAssignmentExpression(
                    syntax, receiver, backingField, BoundNodeFactory.Variable(syntax, newListLocal))));

                var ifStatement = new BoundIfStatement(
                    syntax, isNullCondition,
                    storeSingle,
                    BoundNodeFactory.Block(syntax, growStatements.ToArray()));

                statements.Add(ifStatement);
            }
            else
            {
                // -= 移除首个引用相等匹配：
                // if __old != null {
                //     __idx = -1; __i = 0
                //     while __i < __old.Length { if __idx == -1 && __old[__i] == __h { __idx = __i }; __i++ }
                //     if __idx >= 0 {
                //         if __old.Length == 1 { _<e> = null }
                //         else { 双游标复制跳过 __idx → _<e> = __n }
                //     }
                // }
                var notNullCondition = BoundNodeFactory.Binary(syntax,
                    BoundNodeFactory.Variable(syntax, oldListLocal),
                    SyntaxKind.BangEqualsToken,
                    nullLiteral);

                var scanStatements = new List<BoundStatement>();
                var matchIndexLocal = new LocalVariableSymbol($"__evt{sequence}_idx", isReadOnly: false, TypeSymbol.Int32, null);
                var scanIndexLocal = new LocalVariableSymbol($"__evt{sequence}_j", isReadOnly: false, TypeSymbol.Int32, null);

                scanStatements.Add(new BoundVariableDeclaration(syntax, matchIndexLocal, BoundNodeFactory.Literal(syntax, -1)));
                scanStatements.Add(new BoundVariableDeclaration(syntax, scanIndexLocal, BoundNodeFactory.Literal(syntax, 0)));

                _labelCounter++;
                var scanBreak = new BoundLabel($"__evt{sequence}_scan_br");
                var scanContinue = new BoundLabel($"__evt{sequence}_scan_ct");

                var loopBody = ImmutableArray.CreateBuilder<BoundStatement>();
                var elementEqualsHandler = BoundNodeFactory.Binary(syntax,
                    ElementOf(syntax, oldListLocal, BoundNodeFactory.Variable(syntax, scanIndexLocal)),
                    SyntaxKind.EqualsEqualsToken,
                    BoundNodeFactory.Variable(syntax, handlerLocal));
                var notYetFound = BoundNodeFactory.Binary(syntax,
                    BoundNodeFactory.Variable(syntax, matchIndexLocal),
                    SyntaxKind.EqualsEqualsToken,
                    BoundNodeFactory.Literal(syntax, -1));

                loopBody.Add(new BoundIfStatement(
                    syntax, notYetFound,
                    BoundNodeFactory.Block(syntax, new BoundIfStatement(
                        syntax, elementEqualsHandler,
                        new BoundExpressionStatement(syntax,
                            BoundNodeFactory.Assignment(syntax, matchIndexLocal, BoundNodeFactory.Variable(syntax, scanIndexLocal))),
                        elseStatement: null)),
                    elseStatement: null));
                loopBody.Add(BoundNodeFactory.Increment(syntax, BoundNodeFactory.Variable(syntax, scanIndexLocal)));

                var scanLoop = BoundNodeFactory.While(
                    syntax,
                    BoundNodeFactory.Binary(syntax,
                        BoundNodeFactory.Variable(syntax, scanIndexLocal),
                        SyntaxKind.LessToken,
                        LengthOf(syntax, oldListLocal)),
                    new BoundBlockStatement(syntax, loopBody.ToImmutable()),
                    scanBreak, scanContinue);

                scanStatements.Add(scanLoop);

                // 命中后重建
                var rebuildStatements = new List<BoundStatement>();

                var lengthIsOne = BoundNodeFactory.Binary(syntax,
                    LengthOf(syntax, oldListLocal),
                    SyntaxKind.EqualsEqualsToken,
                    BoundNodeFactory.Literal(syntax, 1));
                var storeNull = new BoundExpressionStatement(
                    syntax,
                    new BoundMemberAssignmentExpression(syntax, receiver, backingField, nullLiteral));

                var compactStatements = new List<BoundStatement>();
                var compactedListLocal = new LocalVariableSymbol($"__evt{sequence}_new", isReadOnly: false, handlerArray, null);
                var targetIndexLocal = new LocalVariableSymbol($"__evt{sequence}_k", isReadOnly: false, TypeSymbol.Int32, null);
                var sourceIndexLocal = new LocalVariableSymbol($"__evt{sequence}_m", isReadOnly: false, TypeSymbol.Int32, null);

                compactStatements.Add(new BoundVariableDeclaration(
                    syntax, compactedListLocal,
                    new BoundArrayCreationExpression(
                        syntax, handlerArray,
                        BoundNodeFactory.Binary(syntax,
                            LengthOf(syntax, oldListLocal),
                            SyntaxKind.MinusToken,
                            BoundNodeFactory.Literal(syntax, 1)),
                        ImmutableArray<BoundExpression>.Empty)));
                compactStatements.Add(new BoundVariableDeclaration(syntax, targetIndexLocal, BoundNodeFactory.Literal(syntax, 0)));
                compactStatements.Add(new BoundVariableDeclaration(syntax, sourceIndexLocal, BoundNodeFactory.Literal(syntax, 0)));

                _labelCounter++;
                var compactBreak = new BoundLabel($"__evt{sequence}_cp_br");
                var compactContinue = new BoundLabel($"__evt{sequence}_cp_ct");

                var copyBody = ImmutableArray.CreateBuilder<BoundStatement>();
                var sourceIsMatch = BoundNodeFactory.Binary(syntax,
                    BoundNodeFactory.Variable(syntax, sourceIndexLocal),
                    SyntaxKind.EqualsEqualsToken,
                    BoundNodeFactory.Variable(syntax, matchIndexLocal));
                var advanceTarget = ImmutableArray.Create<BoundStatement>(
                    new BoundExpressionStatement(syntax, new BoundElementAssignmentExpression(
                        syntax, signature,
                        ElementOf(syntax, compactedListLocal, BoundNodeFactory.Variable(syntax, targetIndexLocal)),
                        ElementOf(syntax, oldListLocal, BoundNodeFactory.Variable(syntax, sourceIndexLocal)))),
                    BoundNodeFactory.Increment(syntax, BoundNodeFactory.Variable(syntax, targetIndexLocal)));

                copyBody.Add(new BoundIfStatement(
                    syntax, sourceIsMatch,
                    BoundNodeFactory.Nop(syntax),
                    BoundNodeFactory.Block(syntax, advanceTarget.ToArray())));
                copyBody.Add(BoundNodeFactory.Increment(syntax, BoundNodeFactory.Variable(syntax, sourceIndexLocal)));

            var compactLoop = BoundNodeFactory.While(
                syntax,
                BoundNodeFactory.Binary(syntax,
                    BoundNodeFactory.Variable(syntax, sourceIndexLocal),
                    SyntaxKind.LessToken,
                    LengthOf(syntax, oldListLocal)),
                new BoundBlockStatement(syntax, copyBody.ToImmutable()),
                compactBreak, compactContinue);

                compactStatements.Add(compactLoop);
                compactStatements.Add(new BoundExpressionStatement(syntax, new BoundMemberAssignmentExpression(
                    syntax, receiver, backingField, BoundNodeFactory.Variable(syntax, compactedListLocal))));                var hitCondition = BoundNodeFactory.Binary(syntax,
                    BoundNodeFactory.Variable(syntax, matchIndexLocal),
                    SyntaxKind.GreaterOrEqualsToken,
                    BoundNodeFactory.Literal(syntax, 0));

                rebuildStatements.Add(new BoundIfStatement(
                    syntax, hitCondition,
                    BoundNodeFactory.Block(syntax,
                        new BoundIfStatement(syntax, lengthIsOne, storeNull, BoundNodeFactory.Block(syntax, compactStatements.ToArray()))),
                    elseStatement: null));

                scanStatements.AddRange(rebuildStatements);

                statements.Add(new BoundIfStatement(
                    syntax, notNullCondition,
                    BoundNodeFactory.Block(syntax, scanStatements.ToArray()),
                    elseStatement: null));
            }

            return BoundNodeFactory.Block(syntax, statements.ToArray());
        }

        /// <summary>
        /// 类内触发脱糖（6e-M22 C5+ 多播）：`e(args)` → 判空 + 快照遍历逐个调用。
        /// 实参只求值一次（提升隐藏局部，防遍历期间重复执行副作用）。
        /// </summary>
        private BoundStatement BindEventRaise(ExpressionStatementSyntax syntax, TextLocation errorLocation, string eventName, SeparatedSyntaxList<ExpressionSyntax> argumentSyntaxes)
        {
            var eventSymbol = _currentClass!.GetEvent(eventName)!;
            var signature = eventSymbol.HandlerType;

            if (signature.ParameterTypes.Length != argumentSyntaxes.Count)
            {
                _diagnostics.ReportWrongArgumentCount(errorLocation, eventName, signature.ParameterTypes.Length, argumentSyntaxes.Count);
                return new BoundBlockStatement(syntax, ImmutableArray<BoundStatement>.Empty);
            }

            _labelCounter++;
            var sequence = _labelCounter;
            var snapshotLocal = new LocalVariableSymbol($"__evt{sequence}_snap", isReadOnly: true, TypeSymbol.ArrayOf(signature), null);
            var indexLocal = new LocalVariableSymbol($"__evt{sequence}_i", isReadOnly: false, TypeSymbol.Int32, null);

            var backingField = _currentClass.GetField("_" + eventName)!;
            var thisReceiver = new BoundThisExpression(syntax.Expression, _currentClass);
            var fieldAccess = new BoundMemberAccessExpression(syntax, snapshotLocal.Type, thisReceiver, backingField.Name, backingField);

            var statements = ImmutableArray.CreateBuilder<BoundStatement>();
            statements.Add(new BoundVariableDeclaration(syntax, snapshotLocal, fieldAccess));

            // 实参求值提升
            var argumentLocals = new LocalVariableSymbol[argumentSyntaxes.Count];
            for (var i = 0; i < argumentSyntaxes.Count; i++)
            {
                argumentLocals[i] = new LocalVariableSymbol($"__evt{sequence}_a{i}", isReadOnly: true, signature.ParameterTypes[i], null);
                statements.Add(new BoundVariableDeclaration(
                    syntax, argumentLocals[i],
                    BindConversion(argumentSyntaxes[i], signature.ParameterTypes[i])));
            }

            // 快照遍历计数器（判空通过后才进入循环，声明置于其前保证线性执行序）
            statements.Add(new BoundVariableDeclaration(syntax, indexLocal, BoundNodeFactory.Literal(syntax, 0)));

            var notNullCondition = BoundNodeFactory.Binary(syntax,
                BoundNodeFactory.Variable(syntax, snapshotLocal),
                SyntaxKind.BangEqualsToken,
                new BoundLiteralExpression(syntax, null, TypeSymbol.Null));

            _labelCounter++;
            var breakLabel = new BoundLabel($"__evt{sequence}_raise_br");
            var continueLabel = new BoundLabel($"__evt{sequence}_raise_ct");

            var loopBody = ImmutableArray.CreateBuilder<BoundStatement>();

            var elementAccess = ElementOf(syntax, snapshotLocal, BoundNodeFactory.Variable(syntax, indexLocal));
            var invocationArguments = argumentLocals
                .Select(local => (BoundExpression)BoundNodeFactory.Variable(syntax, local))
                .ToImmutableArray();
            var invocation = new BoundInvocationExpression(syntax.Expression, elementAccess, invocationArguments, signature.ReturnType);
            loopBody.Add(new BoundExpressionStatement(syntax, invocation));
            loopBody.Add(BoundNodeFactory.Increment(syntax, BoundNodeFactory.Variable(syntax, indexLocal)));

            var raiseLoop = BoundNodeFactory.While(
                syntax,
                BoundNodeFactory.Binary(syntax,
                    BoundNodeFactory.Variable(syntax, indexLocal),
                    SyntaxKind.LessToken,
                    LengthOf(syntax, snapshotLocal)),
                new BoundBlockStatement(syntax, loopBody.ToImmutable()),
                breakLabel, continueLabel);

            statements.Add(new BoundIfStatement(syntax, notNullCondition, raiseLoop, elseStatement: null));

            return BoundNodeFactory.Block(syntax, statements.ToArray());
        }

        /// <summary>`__local.Length` 成员访问合成。</summary>
        private static BoundMemberAccessExpression LengthOf(SyntaxNode syntax, LocalVariableSymbol arrayLocal)
        {
            return new BoundMemberAccessExpression(syntax, TypeSymbol.Int32, BoundNodeFactory.Variable(syntax, arrayLocal), "Length");
        }

        /// <summary>`__local[index]` 元素访问合成。</summary>
        private static BoundElementAccessExpression ElementOf(SyntaxNode syntax, LocalVariableSymbol arrayLocal, BoundExpression index)
        {
            return new BoundElementAccessExpression(syntax, arrayLocal.Type.ElementType!, BoundNodeFactory.Variable(syntax, arrayLocal), index);
        }

        /// <summary>判断字段是否为事件合成后备字段（`_<eventName>`，多播存储）——禁止直接赋值/读取。</summary>
        private static bool IsEventBackingField(FieldSymbol field)
        {
            return field.Name.StartsWith("_", StringComparison.Ordinal) &&
                   field.ContainingClass != null &&
                   field.ContainingClass.GetEvent(field.Name[1..]) != null;
        }

        /// <summary>数组复制循环合成：`while i < source.Length { target[i] = source[i]; i++ }`（target 与 source 等长或更长）。</summary>
        private IEnumerable<BoundStatement> BuildElementCopyLoop(SyntaxNode syntax, LocalVariableSymbol targetLocal, LocalVariableSymbol indexLocal, LocalVariableSymbol sourceLocal, string labelSuffix)
        {
            _labelCounter++;
            var breakLabel = new BoundLabel($"{labelSuffix}{_labelCounter}");
            var continueLabel = new BoundLabel($"{labelSuffix}ct{_labelCounter}");

            var elementType = targetLocal.Type.ElementType!;
            var loopBody = ImmutableArray.Create<BoundStatement>(
                new BoundExpressionStatement(syntax, new BoundElementAssignmentExpression(
                    syntax, elementType,
                    ElementOf(syntax, targetLocal, BoundNodeFactory.Variable(syntax, indexLocal)),
                    ElementOf(syntax, sourceLocal, BoundNodeFactory.Variable(syntax, indexLocal)))),
                BoundNodeFactory.Increment(syntax, BoundNodeFactory.Variable(syntax, indexLocal)));

            yield return BoundNodeFactory.While(
                syntax,
                BoundNodeFactory.Binary(syntax,
                    BoundNodeFactory.Variable(syntax, indexLocal),
                    SyntaxKind.LessToken,
                    LengthOf(syntax, sourceLocal)),
                new BoundBlockStatement(syntax, loopBody),
                breakLabel, continueLabel);
        }

        /// <summary>
        /// delegate 声明绑定（6e-M22 D-A）：合成为 sealed class extends MulticastDelegate + Invoke 方法。
        /// 复用全部类机制（类型查找/is-as/继承链/三后端发射）。
        /// </summary>
        private void BindDelegateDeclaration(DelegateDeclarationSyntax syntax, ClassTypeSymbol classType, List<FunctionSymbol> classFunctions)
        {
            var returnType = BindTypeClause(syntax.ReturnType);
            if (returnType == null)
                return;

            if (ReportByRefDelegateParameters(syntax))
            {
                return;
            }

            var parameters = BindParameters(syntax.Parameters);

            var visibility = GetVisibility(syntax.Modifiers, Visibility.Public);
            var delegateName = syntax.Identifier.Text;

            // 合成 sealed class extends MulticastDelegate
            var delegateClass = new ClassTypeSymbol(delegateName, classType.Namespace, visibility, declaration: null)
            {
                BaseType = ClassTypeSymbol.SystemMulticastDelegate,
                IsSealed = true,
            };

            // Invoke 方法签名匹配 delegate 声明
            var invokeParams = parameters.Select(p => new ParameterSymbol(p.Name, p.Type, p.Ordinal)).ToImmutableArray();
            var invokeFn = new FunctionSymbol("Invoke", invokeParams, returnType, null, containingClass: delegateClass, visibility: Visibility.Public)
            {
                IsStatic = false,
            };
            delegateClass.AddMethod(invokeFn);

            // 注册到类的事件/委托集合（类内 delegate）
            if (!_scope.TryDeclareClass(delegateClass))
            {
                _diagnostics.ReportError(syntax.Identifier.Location, $"delegate '{delegateName}' 已声明。");
            }
        }

        /// <summary>顶层（命名空间级）delegate 声明：同 BindDelegateDeclaration 但注册到全局作用域。</summary>
        internal void BindTopLevelDelegateDeclaration(DelegateDeclarationSyntax syntax, string ns)
        {
            var returnType = BindTypeClause(syntax.ReturnType);
            if (returnType == null)
                return;

            if (ReportByRefDelegateParameters(syntax))
            {
                return;
            }

            var parameters = BindParameters(syntax.Parameters);
            var visibility = GetVisibility(syntax.Modifiers, Visibility.Public);
            var delegateName = syntax.Identifier.Text;
            var fullName = ns.Length == 0 ? delegateName : ns + "." + delegateName;

            var delegateClass = new ClassTypeSymbol(delegateName, ns, visibility, declaration: null)
            {
                BaseType = ClassTypeSymbol.SystemMulticastDelegate,
                IsSealed = true,
            };

            var invokeParams = parameters.Select(p => new ParameterSymbol(p.Name, p.Type, p.Ordinal)).ToImmutableArray();
            var invokeFn = new FunctionSymbol("Invoke", invokeParams, returnType, null, containingClass: delegateClass, visibility: Visibility.Public)
            {
                IsStatic = false,
            };
            delegateClass.AddMethod(invokeFn);

            // 命名空间级 delegate 直接注册进当前作用域（Namespace 属性承载限定）
            _scope.TryDeclareClass(delegateClass);
        }

        /// <summary>delegate 声明 byref 形参拦截（6e-M23 R3）：函数值签名无修饰符概念。有则报诊断并返回 true。</summary>
        private bool ReportByRefDelegateParameters(DelegateDeclarationSyntax syntax)
        {
            foreach (var parameter in syntax.Parameters)
            {
                if (parameter.Modifier != null)
                {
                    _diagnostics.ReportFunctionTypeByRefParameter(parameter.Modifier.Location);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 6e-G7 S6：种子收集辅助 binder 注册 cod 库的泛型定义类名——
        /// 使 BindGenericTypeNameForExpansion 能解析消费方站点的 `Box&lt;i32&gt;` 为实例化类型。
        /// </summary>
        internal void RegisterCodGenericDefinitionsForSeed(ImmutableArray<CodProgram> libraries)
        {
            foreach (var library in libraries)
            {
                foreach (var classType in library.Classes)
                {
                    if (classType.IsGenericDefinition)
                    {
                        _scope.TryDeclareClass(classType);
                    }
                }
            }
        }

        /// <summary>隐式默认构造：类所有部分均未声明构造时生成无参构造。</summary>
        private void DeclareImplicitConstructor(ClassTypeSymbol classType, List<FunctionSymbol> classFunctions, ClassDeclarationSyntax syntax)
        {
            if (classType.GetDeclaredMethod(classType.Name) == null)
            {                var ctor = new FunctionSymbol(classType.Name, ImmutableArray<ParameterSymbol>.Empty, TypeSymbol.Void, null, syntax: syntax, containingClass: classType, visibility: Visibility.Public) { IsConstructor = true };
                classType.AddMethod(ctor);
                classFunctions.Add(ctor);
            }
        }

        /// <summary>隐式静态构造（.cctor）：类含静态字段/静态自动属性初始化器时生成。</summary>
        private void DeclareImplicitStaticConstructor(ClassTypeSymbol classType, List<FunctionSymbol> classFunctions, ClassDeclarationSyntax syntax)
        {
            if (classType.GetDeclaredMethod(".cctor") != null)
            {
                return;
            }

            var hasStaticInitializers = CollectFieldInitializers(classType).Any(fi => fi.Field.IsStatic);
            if (!hasStaticInitializers)
            {
                return;
            }

            var cctor = new FunctionSymbol(".cctor", ImmutableArray<ParameterSymbol>.Empty, TypeSymbol.Void, null,
                syntax: syntax, containingClass: classType, visibility: Visibility.Private) { IsConstructor = true, IsStatic = true };
            classType.AddMethod(cctor);
            classFunctions.Add(cctor);
        }

        /// <summary>收集类的字段/自动属性初始化器（语法级，未绑定）。</summary>
        private static ImmutableArray<(FieldSymbol Field, ExpressionSyntax Initializer)> CollectFieldInitializers(ClassTypeSymbol classType)
        {
            var result = ImmutableArray.CreateBuilder<(FieldSymbol, ExpressionSyntax)>();
            if (classType.Declaration == null)
            {
                return result.ToImmutable();
            }

            foreach (var member in classType.Declaration.Members)
            {
                if (member is ClassFieldDeclarationSyntax fieldDecl && fieldDecl.Initializer != null)
                {
                    var field = classType.GetDeclaredField(fieldDecl.Identifier.Text);
                    if (field != null)
                    {
                        result.Add((field, fieldDecl.Initializer));
                    }
                }
                else if (member is PropertyDeclarationSyntax propDecl && propDecl.Initializer != null && propDecl.IsAuto)
                {
                    var backing = classType.GetDeclaredField("_" + propDecl.Identifier.Text);
                    if (backing != null)
                    {
                        result.Add((backing, propDecl.Initializer));
                    }
                }
            }

            return result.ToImmutable();
        }

        /// <summary>绑定字段初始化器为赋值语句（静态或实例，取决于 isStatic）。</summary>
        private static ImmutableArray<BoundStatement> BindFieldInitializerStatements(Binder binder, ClassTypeSymbol classType, bool isStatic)
        {
            var result = ImmutableArray.CreateBuilder<BoundStatement>();
            foreach (var (field, initializer) in CollectFieldInitializers(classType))
            {
                if (field.IsStatic == isStatic)
                {
                    result.Add(BindFieldInitializer(binder, field, initializer));
                }
            }

            return result.ToImmutable();
        }

        /// <summary>合成字段初始化赋值：`this.field = init`（实例）/ `Class.field = init`（静态）。</summary>
        private static BoundStatement BindFieldInitializer(Binder binder, FieldSymbol field, ExpressionSyntax initializerSyntax)
        {
            var boundInit = binder.BindExpression(initializerSyntax);
            var converted = binder.BindConversion(initializerSyntax.Location, boundInit, field.Type);

            BoundExpression target = field.IsStatic
                ? new BoundStaticTypeExpression(initializerSyntax, field.ContainingClass!)
                : new BoundThisExpression(initializerSyntax, field.ContainingClass!);

            return new BoundExpressionStatement(initializerSyntax, new BoundMemberAssignmentExpression(initializerSyntax, target, field, converted));
        }

        /// <summary>创建接口符号（不可实例化、成员无实现）。</summary>
        private ClassTypeSymbol DeclareInterfaceSymbol(InterfaceDeclarationSyntax syntax, string @namespace)
        {
            var name = syntax.Identifier.Text;
            var visibility = GetVisibility(syntax.Modifiers, Visibility.Internal);

            if (visibility is Visibility.Private or Visibility.Protected)
            {
                _diagnostics.ReportError(syntax.Identifier.Location, $"接口 '{name}' 的可见性只能为 public 或 internal。");
            }

            var classType = new ClassTypeSymbol(name, @namespace, visibility, declaration: null)
            {
                IsInterface = true,
                IsAbstract = true,
            };

            // 泛型类型参数声明（6e-M20）：`interface IEnumerable<T>`（where 子句在阶段 3 绑定）
            classType.TypeParameters = BindClassTypeParameters(syntax.TypeParameters, classType, name);

            if (!_scope.TryDeclareClass(classType))
            {
                _diagnostics.ReportSymbolAlreadyDeclared(syntax.Identifier.Location, name);
            }

            return classType;
        }

        /// <summary>绑定接口声明：基接口列表 + 抽象成员（函数签名/属性访问器）。</summary>
        private void BindInterfaceDeclaration(InterfaceDeclarationSyntax syntax, ClassTypeSymbol interfaceType, List<FunctionSymbol> classFunctions)
        {
            var previousBindingClass = _bindingClass;
            _bindingClass = interfaceType;

            try
            {
                // where 约束（6e-M20；接口符号已全部声明）
                BindWhereClauses(syntax.WhereClauses, interfaceType.TypeParameters);

                // 基接口（仅允许接口；泛型基接口经实参实例化，6e-M20）
                foreach (var baseClause in syntax.BaseTypes)
                {
                    var baseType = BindBaseTypeClause(baseClause);

                    if (baseType == null)
                    {
                        continue;
                    }

                    if (!baseType.IsInterface)
                    {
                        _diagnostics.ReportError(baseClause.Location, $"接口 '{interfaceType.Name}' 只能继承接口，不能继承类 '{baseType.Name}'。");
                    }
                    else
                    {
                        interfaceType.AddBaseInterface(baseType);
                    }
                }

                // 成员：函数签名（抽象）+ 属性访问器（抽象）
                foreach (var member in syntax.Members)
                {
                    if (member is FunctionDeclarationSyntax methodDeclaration)
                    {
                        var visibility = GetVisibility(methodDeclaration.Modifiers, Visibility.Public);

                        // 泛型接口方法类型参数（6e-M20）先行：签名的 T 解析依赖此上下文
                        var previousInterfaceMethodTypeParameters = _declaringMethodTypeParameters;
                        _declaringMethodTypeParameters = BindFunctionTypeParameters(methodDeclaration.TypeParameters);

                        try
                        {
                            var parameters = BindParameters(methodDeclaration.Parameters);
                            var returnType = BindTypeClause(methodDeclaration.Type) ?? TypeSymbol.Void;

                            if (interfaceType.GetDeclaredMethod(methodDeclaration.Identifier.Text) == null)
                            {
                                var method = new FunctionSymbol(methodDeclaration.Identifier.Text, parameters, returnType, methodDeclaration, containingClass: interfaceType, visibility: visibility)
                                {
                                    IsAbstract = true,
                                    IsVirtual = true,
                                    TypeParameters = _declaringMethodTypeParameters,
                                };
                                BindWhereClauses(methodDeclaration.WhereClauses, method.TypeParameters);

                                interfaceType.AddMethod(method);
                                classFunctions.Add(method);
                            }
                            else
                            {
                                _diagnostics.ReportSymbolAlreadyDeclared(methodDeclaration.Identifier.Location, methodDeclaration.Identifier.Text);
                            }
                        }
                        finally
                        {
                            _declaringMethodTypeParameters = previousInterfaceMethodTypeParameters;
                        }
                    }
                    else if (member is PropertyDeclarationSyntax propertyDeclaration)
                    {
                        BindInterfacePropertyDeclaration(propertyDeclaration, interfaceType, classFunctions);
                    }
                }
            }
            finally
            {
                _bindingClass = previousBindingClass;
            }
        }

        /// <summary>接口属性：getter/setter 访问器（无实现、抽象）。</summary>
        private void BindInterfacePropertyDeclaration(PropertyDeclarationSyntax syntax, ClassTypeSymbol interfaceType, List<FunctionSymbol> classFunctions)
        {
            var propertyType = BindTypeClause(syntax.Type);
            var visibility = GetVisibility(syntax.Modifiers, Visibility.Public);

            // 索引器在类侧命名为 "Item"（见 BindPropertyDeclaration），接口侧须保持一致，
            // 否则 IList<T>.this[] 与 List<T>.this[] 因名称（"this" vs "Item"）不匹配，
            // 导致 CheckInterfaceImplementation 报"未实现属性 this"。
            var isIndexer = syntax.Identifier.Text == "this";
            var propertyName = isIndexer ? "Item" : syntax.Identifier.Text;

            // 索引器参数（this[index: i32]）：getter 接收；setter 额外接收 value。
            var indexParams = ImmutableArray<ParameterSymbol>.Empty;
            if (isIndexer)
            {
                indexParams = BindIndexerParameters(syntax.Parameters);
            }

            // 访问器可见性：独立计算 + 严格 C# 校验（CS0273 / 至多一个访问器带修饰符）
            ValidateAccessorVisibility(syntax, visibility);
            var getterVisibility = syntax.Getter != null ? GetVisibility(syntax.Getter.Modifiers, visibility) : visibility;
            var setterVisibility = syntax.Setter != null ? GetVisibility(syntax.Setter.Modifiers, visibility) : visibility;

            if (interfaceType.GetProperty(propertyName) != null)
            {
                _diagnostics.ReportSymbolAlreadyDeclared(syntax.Identifier.Location, propertyName);
                return;
            }

            FunctionSymbol? getter = null;
            if (syntax.Getter != null)
            {
                var getterParams = isIndexer ? indexParams : ImmutableArray<ParameterSymbol>.Empty;
                getter = new FunctionSymbol("get_" + propertyName, getterParams, propertyType, null,
                    syntax: syntax.Getter, containingClass: interfaceType, visibility: getterVisibility)
                {
                    IsAbstract = true,
                    IsVirtual = true,
                };
                interfaceType.AddMethod(getter);
                classFunctions.Add(getter);
            }

            FunctionSymbol? setter = null;
            if (syntax.Setter != null)
            {
                var valueParameter = new ParameterSymbol("value", propertyType, isIndexer ? indexParams.Length : 0);
                var setterParams = isIndexer ? indexParams.Add(valueParameter) : ImmutableArray.Create(valueParameter);
                setter = new FunctionSymbol("set_" + propertyName, setterParams, TypeSymbol.Void, null,
                    syntax: syntax.Setter, containingClass: interfaceType, visibility: setterVisibility)
                {
                    IsAbstract = true,
                    IsVirtual = true,
                };
                interfaceType.AddMethod(setter);
                classFunctions.Add(setter);
            }

            interfaceType.AddProperty(new PropertySymbol(propertyName, propertyType, interfaceType, getter, setter, visibility, isStatic: false, isIndexer: isIndexer));
        }

        /// <summary>接口实现完整性：类（含继承链）须实现其全部接口的每个成员（方法签名/属性访问器）。</summary>
        private void CheckInterfaceImplementation(ClassTypeSymbol classType)
        {
            foreach (var iface in classType.GetAllInterfaces())
            {
                foreach (var method in iface.Methods)
                {
                    if (FindImplementation(classType, method) == null)
                    {
                        _diagnostics.ReportError(classType.Declaration?.Identifier.Location ?? default, $"类 '{classType.Name}' 未实现接口 '{iface.Name}' 的方法 '{method.Name}'。");
                    }
                }

                foreach (var property in iface.Properties)
                {
                    var implementation = classType.GetProperty(property.Name);
                    if (implementation == null)
                    {
                        _diagnostics.ReportError(classType.Declaration?.Identifier.Location ?? default, $"类 '{classType.Name}' 未实现接口 '{iface.Name}' 的属性 '{property.Name}'。");
                        continue;
                    }

                    if (property.Getter != null && implementation.Getter == null)
                    {
                        _diagnostics.ReportError(classType.Declaration?.Identifier.Location ?? default, $"类 '{classType.Name}' 的属性 '{property.Name}' 缺少接口 '{iface.Name}' 要求的 getter。");
                    }

                    if (property.Setter != null && implementation.Setter == null)
                    {
                        _diagnostics.ReportError(classType.Declaration?.Identifier.Location ?? default, $"类 '{classType.Name}' 的属性 '{property.Name}' 缺少接口 '{iface.Name}' 要求的 setter。");
                    }
                }
            }
        }

        /// <summary>查找类（含继承链）中对接口方法的实现：名称 + 参数类型 + 返回类型匹配且 public。</summary>
        private static FunctionSymbol? FindImplementation(ClassTypeSymbol classType, FunctionSymbol interfaceMethod)
        {
            for (var current = classType; current != null; current = current.BaseType)
            {
                foreach (var method in current.GetDeclaredMethods(interfaceMethod.Name))
                {
                    if (method.Visibility != Visibility.Public)
                    {
                        continue;
                    }

                    if (method.Parameters.Length != interfaceMethod.Parameters.Length)
                    {
                        continue;
                    }

                    var parametersMatch = true;
                    for (var i = 0; i < method.Parameters.Length; i++)
                    {
                        if (!TypesMatchForInterfaceImplementation(method.Parameters[i].Type, interfaceMethod.Parameters[i].Type))
                        {
                            parametersMatch = false;
                            break;
                        }
                    }

                    if (!parametersMatch || !TypesMatchForInterfaceImplementation(method.ReturnType, interfaceMethod.ReturnType))
                    {
                        continue;
                    }

                    return method;
                }
            }

            return null;
        }

        /// <summary>
        /// 接口实现签名匹配（6e-M20）：泛型接口的成员签名携带接口自身的类型参数符号，
        /// 与实现类的类型参数符号必然引用不等——结构化递归比较，任一层为类型参数即视为通配。
        /// </summary>
        private static bool TypesMatchForInterfaceImplementation(TypeSymbol implementationType, TypeSymbol interfaceType)
        {
            if (ReferenceEquals(implementationType, interfaceType))
            {
                return true;
            }

            if (implementationType is TypeParameterSymbol || interfaceType is TypeParameterSymbol)
            {
                return true;
            }

            // 协变返回（6e-M20）：实现返回具体枚举器类、接口声明返回接口实例——
            // 按「实现类型的全部接口包含该接口实例（实参通配）」判定
            if (interfaceType is InstantiatedTypeSymbol requiredInterface &&
                requiredInterface.GenericDefinition.IsInterface &&
                implementationType is ClassTypeSymbol implementationClass)
            {
                foreach (var iface in implementationClass.GetAllInterfaces())
                {
                    if (iface is InstantiatedTypeSymbol implemented &&
                        ReferenceEquals(implemented.GenericDefinition, requiredInterface.GenericDefinition) &&
                        implemented.TypeArguments.Length == requiredInterface.TypeArguments.Length)
                    {
                        var argumentsMatch = true;
                        for (var i = 0; i < implemented.TypeArguments.Length; i++)
                        {
                            if (!TypesMatchForInterfaceImplementation(implemented.TypeArguments[i], requiredInterface.TypeArguments[i]))
                            {
                                argumentsMatch = false;
                                break;
                            }
                        }

                        if (argumentsMatch)
                        {
                            return true;
                        }
                    }
                }
            }

            // 嵌套泛型实参逐位递归（IEnumerator$T vs IEnumerator$T' 等）
            if (implementationType is InstantiatedTypeSymbol implInstantiated &&
                interfaceType is InstantiatedTypeSymbol ifaceInstantiated &&
                ReferenceEquals(implInstantiated.GenericDefinition, ifaceInstantiated.GenericDefinition) &&
                implInstantiated.TypeArguments.Length == ifaceInstantiated.TypeArguments.Length)
            {
                for (var i = 0; i < implInstantiated.TypeArguments.Length; i++)
                {
                    if (!TypesMatchForInterfaceImplementation(implInstantiated.TypeArguments[i], ifaceInstantiated.TypeArguments[i]))
                    {
                        return false;
                    }
                }

                return true;
            }

            // 数组元素递归
            if (implementationType.ElementType != null && interfaceType.ElementType != null &&
                implementationType.Kind == SymbolKind.Type && interfaceType.Kind == SymbolKind.Type)
            {
                return TypesMatchForInterfaceImplementation(implementationType.ElementType, interfaceType.ElementType);
            }

            return false;
        }

        /// <summary>自动属性合成体：getter → return _Name；setter → _Name = value。</summary>
        private BoundBlockStatement BindAutoPropertyBody(PropertyAccessorSyntax accessor, FunctionSymbol function)
        {
            var classType = function.ContainingClass!;
            var propName = function.Name.Substring(4); // get_X / set_X → X
            var field = classType.GetDeclaredField("_" + propName);
            if (field == null)
            {
                _diagnostics.ReportError(accessor.Keyword.Location, $"自动属性 '{propName}' 缺少后备字段。");
                return new BoundBlockStatement(accessor, ImmutableArray<BoundStatement>.Empty);
            }

            var thisExpression = new BoundThisExpression(accessor, classType);

            if (accessor.IsGet)
            {
                var memberAccess = new BoundMemberAccessExpression(accessor, field.Type, thisExpression, field.Name, field);
                return new BoundBlockStatement(accessor, ImmutableArray.Create<BoundStatement>(new BoundReturnStatement(accessor, memberAccess)));
            }

            var valueVariable = function.Parameters[0];
            var valueExpression = new BoundVariableExpression(accessor, valueVariable);
            var memberAssignment = new BoundMemberAssignmentExpression(accessor, thisExpression, field, valueExpression);
            return new BoundBlockStatement(accessor, ImmutableArray.Create<BoundStatement>(new BoundExpressionStatement(accessor, memberAssignment)));
        }

        private void BindPropertyDeclaration(PropertyDeclarationSyntax syntax, ClassTypeSymbol classType, List<FunctionSymbol> classFunctions)
        {
            var isIndexer = syntax.Identifier.Text == "this";
            var propertyType = BindTypeClause(syntax.Type);
            var visibility = GetVisibility(syntax.Modifiers, Visibility.Private);
            var isStatic = isIndexer ? false : syntax.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword);
            var isAuto = syntax.IsAuto;

            if (isIndexer && isAuto)
            {
                _diagnostics.ReportError(syntax.Getter?.Body?.Location ?? syntax.Location, "索引器不支持自动属性，必须提供 get/set 访问器主体。");
            }

            // 自动属性：合成后备字段 _Name（索引器禁用自动属性）
            if (isAuto && !isIndexer)
            {
                var backingField = new FieldSymbol("_" + syntax.Identifier.Text, propertyType, visibility, classType, isReadonly: false, isStatic: isStatic);
                classType.AddField(backingField);
            }

            // 访问器可见性：独立计算 + 严格 C# 校验（CS0273 / 至多一个访问器带修饰符）
            ValidateAccessorVisibility(syntax, visibility);
            var getterVisibility = syntax.Getter != null ? GetVisibility(syntax.Getter.Modifiers, visibility) : visibility;
            var setterVisibility = syntax.Setter != null ? GetVisibility(syntax.Setter.Modifiers, visibility) : visibility;

            // 索引器参数（this[a: T]）：getter 接收全部；setter 额外接收 value
            var indexParams = ImmutableArray<ParameterSymbol>.Empty;
            if (isIndexer)
            {
                indexParams = BindIndexerParameters(syntax.Parameters);
            }

            // facade 实例方法降级（隐藏首参 this + 强制静态）；索引器亦遵循
            var lower = !isStatic && classType.IsFacadeClass;

            // getter：get_Name / get_Item
            FunctionSymbol? getter = null;
            if (syntax.Getter != null)
            {
                var getterParams = isIndexer ? indexParams : ImmutableArray<ParameterSymbol>.Empty;
                if (lower)
                {
                    var thisParam = new ParameterSymbol("this", classType.FacadeThisType ?? classType, 0, isThis: true);
                    getterParams = new[] { thisParam }.Concat(getterParams.Select(p => new ParameterSymbol(p.Name, p.Type, p.Ordinal + 1))).ToImmutableArray();
                }

                getter = new FunctionSymbol(isIndexer ? "get_Item" : "get_" + syntax.Identifier.Text, getterParams, propertyType, null,
                    syntax: syntax.Getter, containingClass: classType, visibility: getterVisibility) { IsStatic = isStatic || lower };
                classType.AddMethod(getter);
                classFunctions.Add(getter);
            }

            // setter：set_Name / set_Item（value 隐式参数）
            FunctionSymbol? setter = null;
            if (syntax.Setter != null)
            {
                var valueParameter = new ParameterSymbol("value", propertyType, isIndexer ? indexParams.Length : 0);
                var setterParams = isIndexer ? indexParams.Add(valueParameter) : ImmutableArray.Create(valueParameter);
                if (lower)
                {
                    var thisParam = new ParameterSymbol("this", classType.FacadeThisType ?? classType, 0, isThis: true);
                    setterParams = new[] { thisParam }.Concat(setterParams.Select(p => new ParameterSymbol(p.Name, p.Type, p.Ordinal + 1))).ToImmutableArray();
                }

                setter = new FunctionSymbol(isIndexer ? "set_Item" : "set_" + syntax.Identifier.Text, setterParams, TypeSymbol.Void, null,
                    syntax: syntax.Setter, containingClass: classType, visibility: setterVisibility) { IsStatic = isStatic || lower };
                classType.AddMethod(setter);
                classFunctions.Add(setter);
            }

            var propertyName = isIndexer ? "Item" : syntax.Identifier.Text;
            if (classType.GetDeclaredProperty(propertyName) == null)
            {
                var property = new PropertySymbol(propertyName, propertyType, classType, getter, setter, visibility, isStatic, isIndexer: isIndexer);
                if (getter != null) getter.ContainingProperty = property;
                if (setter != null) setter.ContainingProperty = property;
                classType.AddProperty(property);
            }
            else
            {
                _diagnostics.ReportSymbolAlreadyDeclared(syntax.Identifier.Location, propertyName);
            }
        }

        private ImmutableArray<ParameterSymbol> BindIndexerParameters(ImmutableArray<ParameterSyntax> parameters)
        {
            var builder = ImmutableArray.CreateBuilder<ParameterSymbol>();
            var ordinal = 0;
            foreach (var p in parameters)
            {
                var type = BindTypeClause(p.Type);
                builder.Add(new ParameterSymbol(p.Identifier.Text, type, ordinal));
                ordinal++;
            }

            return builder.ToImmutable();
        }

        private void CollectClasses(NamespaceDeclarationSyntax syntax, string parentNamespace, List<(ClassDeclarationSyntax Syntax, string Namespace)> allClasses)        {
            var ns = parentNamespace.Length == 0 ? syntax.Name : parentNamespace + "." + syntax.Name;

            foreach (var member in syntax.Members)
            {
                if (member is ClassDeclarationSyntax classDeclaration)
                {
                    allClasses.Add((classDeclaration, ns));
                }
                else if (member is NamespaceDeclarationSyntax nested)
                {
                    CollectClasses(nested, ns, allClasses);
                }
            }
        }

        private void CollectInterfaces(NamespaceDeclarationSyntax syntax, string parentNamespace, List<(InterfaceDeclarationSyntax Syntax, string Namespace)> allInterfaces)
        {
            var ns = parentNamespace.Length == 0 ? syntax.Name : parentNamespace + "." + syntax.Name;

            foreach (var member in syntax.Members)
            {
                if (member is InterfaceDeclarationSyntax interfaceDeclaration)
                {
                    allInterfaces.Add((interfaceDeclaration, ns));
                }
                else if (member is NamespaceDeclarationSyntax nested)
                {
                    CollectInterfaces(nested, ns, allInterfaces);
                }
            }
        }

        private void CollectEnums(NamespaceDeclarationSyntax syntax, string parentNamespace, List<(EnumDeclarationSyntax Syntax, string Namespace)> allEnums)
        {
            var ns = parentNamespace.Length == 0 ? syntax.Name : parentNamespace + "." + syntax.Name;

            foreach (var member in syntax.Members)
            {
                if (member is EnumDeclarationSyntax enumDeclaration)
                {
                    allEnums.Add((enumDeclaration, ns));
                }
                else if (member is NamespaceDeclarationSyntax nested)
                {
                    CollectEnums(nested, ns, allEnums);
                }
            }
        }

        private void CollectNamespaceFunctions(NamespaceDeclarationSyntax syntax, string parentNamespace, string? importedDll, List<(FunctionDeclarationSyntax Syntax, string Namespace, string? Dll)> functions)
        {
            var ns = parentNamespace.Length == 0 ? syntax.Name : parentNamespace + "." + syntax.Name;

            foreach (var member in syntax.Members)
            {
                if (member is FunctionDeclarationSyntax functionDeclaration)
                {
                    functions.Add((functionDeclaration, ns, importedDll));
                }
                else if (member is NamespaceDeclarationSyntax nested)
                {
                    CollectNamespaceFunctions(nested, ns, importedDll, functions);
                }
            }
        }

        /// <summary>递归收集命名空间内（含文件作用域 `namespace Foo;`）的 using 指令，供名称解析与 6e-M15 警告。</summary>
        private void CollectNamespaceUsings(NamespaceDeclarationSyntax syntax, List<string> usingNamespaces, List<UsingDirectiveSyntax> usingDirectives)
        {
            foreach (var member in syntax.Members)
            {
                if (member is UsingDirectiveSyntax usingDirective)
                {
                    CollectUsingDirective(usingDirective);
                    usingDirectives.Add(usingDirective);
                }
                else if (member is NamespaceDeclarationSyntax nested)
                {
                    CollectNamespaceUsings(nested, usingNamespaces, usingDirectives);
                }
            }
        }

        /// <summary>按形态收集 using：`using static <类>` → _usingStatics；`using <别名> = <名>` → _usingAliases；否则 → _usingNamespaces。</summary>
        private void CollectUsingDirective(UsingDirectiveSyntax directive)
        {
            if (directive.StaticKeyword != null)
            {
                _usingStatics.Add(directive.Name);
            }
            else if (directive.Alias.Length > 0)
            {
                _usingAliases[directive.Alias] = directive.Name;
            }
            else
            {
                _usingNamespaces.Add(directive.Name);
            }
        }

        /// <summary>using 未解析警告（6e-M15）：命名空间在程序声明 / 引用程序集 / .cod 库中都找不到时发警告（提示不绑定 .NET BCL）。</summary>
        private void ReportUnresolvedUsings(
            List<UsingDirectiveSyntax> usingDirectives,
            List<(ClassDeclarationSyntax Syntax, string Namespace)> allClasses,
            List<(InterfaceDeclarationSyntax Syntax, string Namespace)> allInterfaces,
            List<(EnumDeclarationSyntax Syntax, string Namespace)> allEnums,
            List<(FunctionDeclarationSyntax Syntax, string Namespace, string? Dll)> pendingFunctions,
            ImmutableArray<CodProgram> codLibraries)
        {
            if (usingDirectives.Count == 0)
            {
                return;
            }

            var knownNamespaces = new HashSet<string>(StringComparer.Ordinal);
            void AddNamespacePrefixes(string ns)
            {
                while (ns.Length > 0)
                {
                    knownNamespaces.Add(ns);
                    var dot = ns.LastIndexOf('.');
                    if (dot < 0)
                    {
                        break;
                    }

                    ns = ns.Substring(0, dot);
                }
            }

            var knownClasses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (syntax, ns) in allClasses)
            {
                knownClasses.Add(ns.Length == 0 ? syntax.Identifier.Text : ns + "." + syntax.Identifier.Text);
            }

            foreach (var (_, ns) in allClasses) AddNamespacePrefixes(ns);
            foreach (var (_, ns) in allInterfaces) AddNamespacePrefixes(ns);
            foreach (var (_, ns) in allEnums) AddNamespacePrefixes(ns);
            foreach (var (_, ns, _) in pendingFunctions) AddNamespacePrefixes(ns);
            foreach (var library in codLibraries)
            {
                foreach (var ns in library.Namespaces)
                {
                    AddNamespacePrefixes(ns);
                }

                foreach (var cls in library.Classes)
                {
                    knownClasses.Add(cls.FullName);
                }
            }

            var metadataReader = _references.Length == 0 ? null : new MetadataReader(_references.ToArray());
            foreach (var directive in usingDirectives)
            {
                var name = directive.Name;

                // `using static <类>`：目标必须是类（6e-M18）
                if (directive.StaticKeyword != null)
                {
                    if (!knownClasses.Contains(name))
                    {
                        _diagnostics.ReportUsingStaticTargetNotClass(directive.Location, name);
                    }

                    continue;
                }

                // `using <别名> = <名>`：目标须为命名空间或类（无论解析成功与否都终止于本分支）
                if (directive.Alias.Length > 0)
                {
                    if (!knownNamespaces.Contains(name) && !knownClasses.Contains(name))
                    {
                        _diagnostics.ReportUnresolvedUsing(directive.Location, name);
                    }

                    continue;
                }

                if (knownNamespaces.Contains(name))
                {
                    continue;
                }

                if (metadataReader != null && metadataReader.NamespaceExists(name))
                {
                    continue;
                }

                _diagnostics.ReportUnresolvedUsing(directive.Location, name);
            }
        }

        private FunctionSymbol BindClassMethodDeclaration(FunctionDeclarationSyntax syntax, ClassTypeSymbol classType, string? dllName = null, CharSet? blockCharSet = null)
        {
            // 泛型方法类型参数（6e-M20）先行落符号：签名 T 解析依赖此上下文
            var previousMethodTypeParameters = _declaringMethodTypeParameters;
            _declaringMethodTypeParameters = BindFunctionTypeParameters(syntax.TypeParameters);

            try
            {
                return BindClassMethodDeclarationCore(syntax, classType, dllName, blockCharSet);
            }
            finally
            {
                _declaringMethodTypeParameters = previousMethodTypeParameters;
            }
        }

        private FunctionSymbol BindClassMethodDeclarationCore(FunctionDeclarationSyntax syntax, ClassTypeSymbol classType, string? dllName = null, CharSet? blockCharSet = null)
        {
            var parameters = BindParameters(syntax.Parameters);
            var type = BindTypeClause(syntax.Type) ?? TypeSymbol.Void;
            var isSyscall = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.SyscallKeyword);
            var isExtern = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.CdeclKeyword || m.Kind == SyntaxKind.StdcallKeyword) ||
                           syntax.ExternMetadata != null;
            // syscall/extern 方法缺省 public（System.Runtime.Runtime.Print 供 System.Console 封装层调用；extern 供类外限定调用）
            var visibility = GetVisibility(syntax.Modifiers, (isSyscall || isExtern) ? Visibility.Public : Visibility.Private);
            var isStatic = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword);

            // 6e-M19 M2-b：facade 类实例方法编译期降级——隐藏首参 this（类型 = 承载类型）+ 强制静态，
            // 三后端按普通静态容器方法发射（对齐 C# 基元别名模型：Int32.ToString 等成员面载体）。
            // 声明参数 ordinal 整体 +1（真静态无 instance offset，this 占据 arg0）
            if (!isStatic && !isSyscall && !isExtern && classType.IsFacadeClass)
            {
                isStatic = true;
                var thisParameter = new ParameterSymbol("this", classType.FacadeThisType ?? classType, 0, isThis: true);
                var shifted = parameters.Select(p => new ParameterSymbol(p.Name, p.Type, p.Ordinal + 1)).ToArray();
                parameters = new[] { thisParameter }.Concat(shifted).ToImmutableArray();
            }

            var isVirtual = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.VirtualKeyword);
            var isOverride = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.OverrideKeyword);
            var isAbstract = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.AbstractKeyword);
            var isSealed = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.SealedKeyword);

            BuiltinKind? builtinKind = null;
            if (isSyscall)
            {
                var builtin = BuiltinFunctions.GetByName(syntax.Identifier.Text);
                if (builtin == null)
                {
                    _diagnostics.ReportSyscallFunctionUnknown(syntax.Identifier.Location, syntax.Identifier.Text);
                }
                else
                {
                    builtinKind = builtin.BuiltinKind;
                }

                if (syntax.Body != null)
                {
                    _diagnostics.ReportSyscallFunctionCannotHaveBody(syntax.Body.Location);
                }
            }

            // 6e-M17 Step 4：extern 校验 —— 在 import 块内（dllName != null）必须 static 且不能有 body；
            // 在 import 块外声明 extern（stdcall/cdecl 方法）→ 报错（须进 import 块）
            if (isExtern)
            {
                if (dllName == null)
                {
                    _diagnostics.ReportExternFunctionMustBeInImportBlock(syntax.Identifier.Location);
                }

                if (!isStatic)
                {
                    _diagnostics.ReportExternFunctionMustBeStatic(syntax.Identifier.Location);
                }

                if (syntax.Body != null)
                {
                    _diagnostics.ReportExternFunctionCannotHaveBody(syntax.Body.Location);
                }
            }

            // 6e-M17 Step 5：extern 元数据（entry 别名 + charset 编码）——函数级覆盖块级/缺省
            string? entryPoint = null;
            CharSet? charSet = blockCharSet;
            if (syntax.ExternMetadata != null)
            {
                foreach (var argument in syntax.ExternMetadata.Arguments)
                {
                    switch (argument.Key.Text)
                    {
                        case "entry":
                            entryPoint = argument.Value.Text;
                            break;
                        case "charset":
                            charSet = ParseCharSetValue(argument.Value);
                            break;
                        default:
                            _diagnostics.ReportError(argument.Key.Location, $"未知 extern 元数据键 '{argument.Key.Text}'（支持 entry / charset，未来 setlasterror/exactspelling 预留）。");
                            break;
                    }
                }
            }

            // syscall 方法隐含 static（System.Runtime.Runtime.Print 类名调用）
            var method = new FunctionSymbol(syntax.Identifier.Text, parameters, type, syntax, isExtern: isExtern, dllName: dllName, callingConvention: GetCallingConvention(syntax), containingClass: classType, visibility: visibility, builtinKind: builtinKind, entryPoint: entryPoint, charSet: charSet)
            {
                IsStatic = isStatic || isSyscall,
                IsVirtual = isVirtual,
                IsOverride = isOverride,
                IsAbstract = isAbstract,
                IsSealed = isSealed,
            };

            // 泛型方法类型参数（6e-M20）：`function Map<U>(…)` 类内声明 + where 子句落符号
            method.TypeParameters = _declaringMethodTypeParameters;
            BindWhereClauses(syntax.WhereClauses, method.TypeParameters);

            // override 语义（6e-M19 M2-c 升级）：沿基类链找同签名 virtual/abstract 方法——
            // 参数个数/类型逐一相同 + 返回类型相同（C# CS0115/CS1715 对齐，协变返回不做）
            if (isOverride)
            {
                if (!HasBaseClass(classType))
                {
                    _diagnostics.ReportError(syntax.Identifier.Location, $"方法 '{syntax.Identifier.Text}' 标记 override，但类型没有基类。");
                }
                else
                {
                    var candidates = classType.BaseType!.GetMethods(syntax.Identifier.Text)
                        .Where(m => (m.IsVirtual || m.IsAbstract) && !m.IsSealed)
                        .ToImmutableArray();

                    FunctionSymbol? baseMethod = null;
                    foreach (var candidate in candidates)
                    {
                        if (IsOverrideSignatureMatch(candidate, method))
                        {
                            baseMethod = candidate;
                            break;
                        }
                    }

                    if (baseMethod == null)
                    {
                        if (candidates.IsEmpty)
                        {
                            _diagnostics.ReportError(syntax.Identifier.Location, $"基类中找不到可重写的 virtual/abstract 方法 '{syntax.Identifier.Text}'。");
                        }
                        else
                        {
                            var nearest = classType.BaseType.GetMethod(syntax.Identifier.Text);
                            _diagnostics.ReportOverrideSignatureMismatch(syntax.Identifier.Location, syntax.Identifier.Text, nearest?.ReturnType ?? method.ReturnType, method.ReturnType);
                        }
                    }
                    else
                    {
                        method.OverriddenMethod = baseMethod;
                    }
                }
            }
            else if (isVirtual && classType.BaseType?.GetMethod(syntax.Identifier.Text)?.IsOverride == true)
            {
                // 隐藏基类 override 方法（允许，IL newslot）
            }

            return method;
        }

        private static bool IsOverrideSignatureMatch(FunctionSymbol baseMethod, FunctionSymbol overrideMethod)
        {
            if (baseMethod.ReturnType != overrideMethod.ReturnType)
            {
                return false;
            }

            if (baseMethod.Parameters.Length != overrideMethod.Parameters.Length)
            {
                return false;
            }

            for (var i = 0; i < baseMethod.Parameters.Length; i++)
            {
                if (baseMethod.Parameters[i].Type != overrideMethod.Parameters[i].Type ||
                    baseMethod.Parameters[i].IsOut != overrideMethod.Parameters[i].IsOut ||
                    baseMethod.Parameters[i].IsRef != overrideMethod.Parameters[i].IsRef)
                {
                    return false;
                }
            }

            return true;
        }

        private static CallingConvention GetCallingConvention(FunctionDeclarationSyntax syntax)
        {
            return syntax.Modifiers.Select(m => m.Kind)
                .FirstOrDefault(k => k == SyntaxKind.CdeclKeyword || k == SyntaxKind.StdcallKeyword) switch
            {
                SyntaxKind.CdeclKeyword => CallingConvention.Cdecl,
                SyntaxKind.StdcallKeyword => CallingConvention.StdCall,
                _ => CallingConvention.Winapi,
            };
        }

        /// <summary>
        /// 绑定 import 块（6e-M17 Step 4）：`import <dll> { static extern ... }`。
        /// 块内成员只允许 extern 函数声明，DLL 归属由块声明式绑定；外部使用类名限定调用（`Kernel32.GetTickCount()`）。
        /// </summary>
        private void BindImportBlock(ImportBlockSyntax importBlock, ClassTypeSymbol classType, List<FunctionSymbol> classFunctions)
        {
            // 块级 charset 键（6e-M17 Step 5）：块内函数缺省编码；缺省 unicode
            var blockCharSet = importBlock.CharsetKey != null
                ? ParseCharSetValue(importBlock.CharsetValue)
                : CharSet.Unicode;

            foreach (var blockMember in importBlock.Members)
            {
                if (blockMember is FunctionDeclarationSyntax functionDeclaration)
                {
                    // 块内只允许 extern 函数声明（stdcall/cdecl 或带 extern 元数据）；普通带体函数 → 诊断
                    var isExternDecl = functionDeclaration.Modifiers.Any(m => m.Kind == SyntaxKind.CdeclKeyword || m.Kind == SyntaxKind.StdcallKeyword) ||
                                       functionDeclaration.ExternMetadata != null;
                    if (!isExternDecl)
                    {
                        _diagnostics.ReportImportBlockOnlyExternFunctions(functionDeclaration.Identifier.Location);
                    }

                    var method = BindClassMethodDeclaration(functionDeclaration, classType, dllName: importBlock.DllName, blockCharSet: blockCharSet);

                    if (!classType.HasDeclaredMethodSignature(functionDeclaration.Identifier.Text, method))
                    {
                        classType.AddMethod(method);
                        classFunctions.Add(method);
                    }
                    else
                    {
                        _diagnostics.ReportSymbolAlreadyDeclared(functionDeclaration.Identifier.Location, functionDeclaration.Identifier.Text);
                    }
                }
                else
                {
                    _diagnostics.ReportImportBlockOnlyExternFunctions(blockMember.Location);
                }
            }
        }

        /// <summary>解析 charset 值文本（`ansi` / `unicode` / `auto`）；未知值 → unicode + 诊断。</summary>
        private CharSet ParseCharSetValue(SyntaxToken? valueToken)
        {
            if (valueToken == null)
            {
                return CharSet.Unicode;
            }

            switch (valueToken.Text)
            {
                case "ansi":
                    return CharSet.Ansi;
                case "auto":
                    return CharSet.Auto;
                case "unicode":
                    return CharSet.Unicode;
                default:
                    _diagnostics.ReportError(valueToken.Location, $"未知 charset 值 '{valueToken.Text}'（支持 ansi / unicode / auto）。");
                    return CharSet.Unicode;
            }
        }

        private BoundConstructorChainExpression? BindConstructorChain(ConstructorDeclarationSyntax syntax, ClassTypeSymbol classType)
        {
            var isBase = syntax.InitializerKeyword!.Kind == SyntaxKind.BaseKeyword;
            var targetClass = isBase ? classType.BaseType : classType;

            if (targetClass == null)
            {
                _diagnostics.ReportError(syntax.InitializerKeyword!.Location, "类型没有基类，不能调用 base(...)。");
                return null;
            }

            // 6e-M19 M2-c：显式链到内建 System.Object——仅 0 参（无 .ctor 符号，等价 CLR 隐式基构造 no-op）
            if (isBase && SystemObjectMembers.IsBuiltinSystemClass(targetClass))
            {
                if (syntax.InitializerArguments.Count == 0)
                {
                    return new BoundConstructorChainExpression(syntax, ConstructorInitializerKind.Base, constructor: null, ImmutableArray<BoundExpression>.Empty);
                }

                _diagnostics.ReportError(syntax.InitializerKeyword!.Location, "System.Object 没有带参数的构造函数。");
                return null;
            }

            var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
            foreach (var argumentSyntax in syntax.InitializerArguments)
            {
                arguments.Add(BindExpression(argumentSyntax));
            }

            var ctorName = targetClass.Name;
            var candidates = targetClass.Methods.Where(m => m.Name == ctorName && (isBase || m != _function)).ToArray();

            FunctionSymbol? target = null;
            foreach (var candidate in candidates)
            {
                if (candidate.Parameters.Length != arguments.Count)
                {
                    continue;
                }

                var match = true;
                for (var i = 0; i < arguments.Count; i++)
                {
                    if (arguments[i].Type != candidate.Parameters[i].Type)
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    target = candidate;
                    break;
                }
            }

            if (target == null)
            {
                _diagnostics.ReportWrongArgumentCount(syntax.InitializerKeyword!.Location, (isBase ? "base" : "this"), candidates.Length > 0 ? candidates[0].Parameters.Length : 0, arguments.Count);
                return null;
            }

            for (var i = 0; i < arguments.Count; i++)
            {
                arguments[i] = BindConversion(arguments[i].Syntax.Location, arguments[i], target.Parameters[i].Type);
            }

            return new BoundConstructorChainExpression(syntax, isBase ? ConstructorInitializerKind.Base : ConstructorInitializerKind.This, target, arguments.ToImmutable());
        }

        private static BoundScope CreateParentScope(BoundGlobalScope? previous)
        {
            var stack = new Stack<BoundGlobalScope>();
            while (previous != null)
            {
                stack.Push(previous);
                previous = previous.Previous;
            }

            var parent = CreateRootScope();

            while (stack.Count > 0)
            {
                previous = stack.Pop();
                var scope = new BoundScope(parent);

                foreach (var f in previous.Functions)
                {
                    // class 方法/构造不进入全局函数作用域（用限定访问/this 解析），仅顶层函数可裸调用
                    if (f.ContainingClass != null)
                    {
                        continue;
                    }

                    scope.TryDeclareFunction(f);

                    // 命名空间函数同步进命名空间表（`Foo.Add(...)` 限定访问）
                    if (f.Namespace.Length > 0)
                    {
                        scope.TryDeclareNamespaceFunction(f.Namespace, f);
                    }
                }

                foreach (var e in previous.Enums)
                {
                    scope.TryDeclareEnum(e);
                }

                foreach (var c in previous.Classes)
                {
                    scope.TryDeclareClass(c);
                }

                foreach (var v in previous.Variables)
                {
                    scope.TryDeclareVariable(v);
                }

                parent = scope;
            }

            return parent;
        }

        private static BoundScope CreateRootScope()
        {
            var result = new BoundScope(null);

            // 6e-M17 Step 3：移除内置函数隐式注入（C# 式强隔离）——print/input/random 等
            // 不再全局裸可用；用户须 `using System.Console` 后 WriteLine/ReadLine，或
            // 经 System.Runtime（syscall 容器类，SystemLibrary 内建嵌入）显式调用。

            return result;
        }

        /// <summary>把 `.cod` 库的公共符号注入作用域（v1 无命名空间 → 裸注册；非空命名空间留扩展位，.cod v2 时启用）。</summary>
        private static void InjectCodSymbols(BoundScope scope, ImmutableArray<CodProgram> codLibraries)
        {
            if (codLibraries.IsDefaultOrEmpty)
            {
                return;
            }

            foreach (var library in codLibraries)
            {
                foreach (var function in library.Functions)
                {
                    if (function.ContainingClass == null)
                    {
                        if (function.Namespace.Length == 0)
                        {
                            scope.TryDeclareFunction(function);
                        }
                        else
                        {
                            scope.TryDeclareNamespaceFunction(function.Namespace, function);
                        }
                    }
                }

                foreach (var enumType in library.Enums)
                {
                    scope.TryDeclareEnum(enumType);
                }

                // 容器类注入（6e-M17）：类壳注册进类型表；其方法已随 Functions 注入（ContainingClass 指向本类）
                foreach (var classType in library.Classes)
                {
                    // 6e-M19 M2-b：facade 标记不序列化，注入侧按全名映射表补齐
                    if (!classType.IsFacadeClass && FacadeTargets.ContainsKey(classType.FullName))
                    {
                        classType.IsFacadeClass = true;
                        classType.FacadeThisType = FacadeTargets[classType.FullName];
                    }

                    scope.TryDeclareClass(classType);
                }
            }
        }

        public DiagnosticBag Diagnostics => _diagnostics;

        private BoundStatement BindErrorStatement(SyntaxNode syntax)
        {
            return new BoundExpressionStatement(syntax, new BoundErrorExpression(syntax));
        }

        private BoundStatement BindGlobalStatement(StatementSyntax syntax)
        {
            return BindStatement(syntax, isGlobal: true);
        }

        private BoundStatement BindStatement(StatementSyntax syntax, bool isGlobal = false)
        {
            var result = BindStatementInternal(syntax);

            if (!_isScript || !isGlobal)
            {
                    if (result is BoundExpressionStatement es)
                {
                    var isAllowedExpression = es.Expression.Kind == BoundNodeKind.ErrorExpression ||
                                              es.Expression.Kind == BoundNodeKind.AssignmentExpression ||
                                              es.Expression.Kind == BoundNodeKind.CallExpression ||
                                              es.Expression.Kind == BoundNodeKind.InvocationExpression ||
                                              es.Expression.Kind == BoundNodeKind.CompoundAssignmentExpression ||
                                              es.Expression.Kind == BoundNodeKind.ConditionalExpression ||
                                              es.Expression.Kind == BoundNodeKind.ElementAssignmentExpression ||
                                              es.Expression.Kind == BoundNodeKind.MemberAssignmentExpression ||
                                              es.Expression.Kind == BoundNodeKind.MemberCallExpression;

                    if (!isAllowedExpression)
                        _diagnostics.ReportInvalidExpressionStatement(syntax.Location);
                }
            }

            return result;
        }

        private BoundStatement BindStatementInternal(StatementSyntax syntax)
        {
            switch (syntax.Kind)
            {
                case SyntaxKind.BlockStatement: return BindBlockStatement((BlockStatementSyntax)syntax);
                case SyntaxKind.VariableDeclaration: return BindVariableDeclaration((VariableDeclarationSyntax)syntax);
                case SyntaxKind.IfStatement: return BindIfStatement((IfStatementSyntax)syntax);
                case SyntaxKind.WhileStatement: return BindWhileStatement((WhileStatementSyntax)syntax);
                case SyntaxKind.DoWhileStatement: return BindDoWhileStatement((DoWhileStatementSyntax)syntax);
                case SyntaxKind.ForStatement: return BindForStatement((ForStatementSyntax)syntax);
                case SyntaxKind.ForeachStatement: return BindForeachStatement((ForeachStatementSyntax)syntax);
                case SyntaxKind.SwitchStatement: return BindSwitchStatement((SwitchStatementSyntax)syntax);
                case SyntaxKind.CSStyleForStatement: return BindCSStyleForStatement((CSStyleForStatementSyntax)syntax);
                case SyntaxKind.BreakStatement: return BindBreakStatement((BreakStatementSyntax)syntax);
                case SyntaxKind.ContinueStatement: return BindContinueStatement((ContinueStatementSyntax)syntax);
                case SyntaxKind.ReturnStatement: return BindReturnStatement((ReturnStatementSyntax)syntax);
                case SyntaxKind.ThrowStatement: return BindThrowStatement((ThrowStatementSyntax)syntax);
                case SyntaxKind.TryStatement: return BindTryStatement((TryStatementSyntax)syntax);
                case SyntaxKind.ExpressionStatement: return BindExpressionStatement((ExpressionStatementSyntax)syntax);
                default:
                    throw new Exception($"Unexcepted syntax {syntax.Kind}");
            }
        }

        private BoundStatement BindBlockStatement(BlockStatementSyntax syntax)
        {
            var statements = ImmutableArray.CreateBuilder<BoundStatement>();
            _scope = new BoundScope(_scope);

            foreach (var statementSyntax in syntax.Statements)
            {
                var statement = BindStatement(statementSyntax);
                statements.Add(statement);
            }

            _scope = _scope.Parent!;

            return new BoundBlockStatement(syntax, statements.ToImmutable());
        }

        private BoundStatement BindVariableDeclaration(VariableDeclarationSyntax syntax)
        {
            var isReadOnly = syntax.Keyword?.Kind == SyntaxKind.LetKeyword ||
                             syntax.Keyword?.Kind == SyntaxKind.ConstKeyword;
            var type = BindTypeClause(syntax.TypeClause);
            var initializer = syntax.Initializer == null ? null : BindExpression(syntax.Initializer);
            var variableType = type ?? initializer?.Type ?? TypeSymbol.Error;

            // 6e-M19 M5-a：var 无法从 null 推断类型（对齐 C# CS8374）——防 Null 单例泄漏成变量类型
            if (type == null && variableType == TypeSymbol.Null)
            {
                _diagnostics.ReportCannotInferVarFromNull(syntax.Location);

                var errorVariable = BindVariableDeclaration(syntax.Identifier, isReadOnly, TypeSymbol.Error);
                return new BoundVariableDeclaration(syntax, errorVariable, new BoundErrorExpression(syntax.Identifier));
            }

            if (initializer == null)
            {
                if (syntax.Keyword?.Kind == SyntaxKind.LetKeyword ||
                    syntax.Keyword?.Kind == SyntaxKind.ConstKeyword)
                {
                    _diagnostics.ReportError(syntax.Location, $"{syntax.Keyword.Text} 变量必须提供初始值。");
                }
                else if (syntax.TypeClause == null)
                {
                    _diagnostics.ReportError(syntax.Location, "变量声明必须指定类型或初始值。");
                }

                if (variableType == TypeSymbol.Error)
                {
                    var errorExpression = new BoundErrorExpression(syntax.Identifier);
                    var errorVariable = BindVariableDeclaration(syntax.Identifier, isReadOnly, TypeSymbol.Error);

                    return new BoundVariableDeclaration(syntax, errorVariable, errorExpression);
                }

                initializer = new BoundLiteralExpression(syntax, GetDefaultValue(variableType), variableType);
            }

            var variable = BindVariableDeclaration(syntax.Identifier, isReadOnly, variableType, initializer.ConstantValue);
            var convertedInitializer = BindConversion(syntax.Initializer?.Location ?? syntax.Location, initializer, variableType);

            return new BoundVariableDeclaration(syntax, variable, convertedInitializer);
        }

        private static object GetDefaultValue(TypeSymbol type)
        {
            if (type == TypeSymbol.Boolean)
            {
                return false;
            }

            if (type == TypeSymbol.Int32 || type == TypeSymbol.UInt8)
            {
                return 0;
            }

            if (type == TypeSymbol.Int64)
            {
                return 0L;
            }

            if (type == TypeSymbol.Char)
            {
                return '\0';
            }

            if (type == TypeSymbol.Double)
            {
                return 0.0;
            }

            if (type == TypeSymbol.String || type is ClassTypeSymbol || type.ElementType != null)
            {
                return null!;
            }

            if (type is EnumTypeSymbol)
            {
                return 0;
            }

            throw new System.Exception($"Unexpected type {type}");
        }

        [return: NotNullIfNotNull(nameof(syntax))]
        private BoundExpression BindBaseExpression(BaseExpressionSyntax syntax)
        {
            if (_currentClass == null)
            {
                _diagnostics.ReportError(syntax.Location, "base 只能用在类的实例方法或构造函数中。");
                return new BoundErrorExpression(syntax);
            }

            if (_function?.IsStatic == true)
            {
                _diagnostics.ReportError(syntax.Location, "静态方法中不能使用 base。");
                return new BoundErrorExpression(syntax);
            }

            if (!HasBaseClass(_currentClass))
            {
                _diagnostics.ReportError(syntax.Location, $"类型 {_currentClass.Name} 没有基类，不能使用 base。");
                return new BoundErrorExpression(syntax);
            }

            return new BoundBaseExpression(syntax, _currentClass.BaseType);
        }

        private BoundExpression BindThisExpression(ThisExpressionSyntax syntax)
        {
            if (_currentClass == null)
            {
                _diagnostics.ReportError(syntax.Location, "this 只能用在类的实例方法或构造函数中。");
                return new BoundErrorExpression(syntax);
            }

            // 6e-M19 M2-b：facade 降级方法（静态化 + 隐藏首参 this）——this 解析为首参变量
            if (_function?.IsStatic == true)
            {
                var hiddenThis = _currentClass.IsFacadeClass
                    ? _function.Parameters.FirstOrDefault(p => p.Ordinal == 0 && p.Name == "this")
                    : null;
                if (hiddenThis != null)
                {
                    return new BoundVariableExpression(syntax, hiddenThis);
                }

                _diagnostics.ReportError(syntax.Location, "静态方法中不能使用 this。");
                return new BoundErrorExpression(syntax);
            }

            return new BoundThisExpression(syntax, _currentClass);
        }

        private BoundExpression BindCastExpression(CastExpressionSyntax syntax)
        {
            var type = LookupType(syntax.TypeName.Text ?? "?");
            if (type == null)
            {
                _diagnostics.ReportUndefinedType(syntax.TypeName.Location, syntax.TypeName.Text ?? "?");
                return new BoundErrorExpression(syntax);
            }

            return BindConversion(syntax.Expression, type, allowExplicit: true);
        }

        /// <summary>6e-M19 M5-b：is 类型测试——静态可判定折叠，仅"接收者为目标的严格基类/接口"产生动态节点。</summary>
        private BoundExpression BindIsExpression(IsExpressionSyntax syntax)
        {
            return BindTypeTestOrAs(syntax.Expression, syntax.TypeName, syntax, wantBool: true);
        }

        /// <summary>6e-M19 M5-b：as 类型转换——同 is 的静态判定；动态情形失败得 null。</summary>
        private BoundExpression BindAsExpression(AsExpressionSyntax syntax)
        {
            return BindTypeTestOrAs(syntax.Expression, syntax.TypeName, syntax, wantBool: false);
        }

        private BoundExpression BindTypeTestOrAs(ExpressionSyntax expressionSyntax, SyntaxToken typeName, ExpressionSyntax ownerSyntax, bool wantBool)
        {
            var target = LookupType(typeName.Text ?? "?");
            if (target == null)
            {
                _diagnostics.ReportUndefinedType(typeName.Location, typeName.Text ?? "?");
                return new BoundErrorExpression(ownerSyntax);
            }

            if (target.IsPlaceholder128)
            {
                _diagnostics.ReportUnsupported128BitType(typeName.Location, typeName.Text ?? "?");
                return new BoundErrorExpression(ownerSyntax);
            }

            // 目标约束：非接口类或 string（接口分派 native 未实现、数组无类型对象——三后端一致先拒）
            var targetClass = target as ClassTypeSymbol;

            // `is/as String` 解析为 System.String 承载类（facade/外部）→ 归一为基元 string
            if (targetClass != null && targetClass.FullName == "System.String")
            {
                target = TypeSymbol.String;
                targetClass = null;
            }

            if ((targetClass != null && targetClass.IsInterface) || target.ElementType != null ||
                (targetClass == null && target != TypeSymbol.String))
            {
                _diagnostics.ReportIsAsUnsupportedTarget(typeName.Location, typeName.Text ?? "?");
                return new BoundErrorExpression(ownerSyntax);
            }

            var operand = BindExpression(expressionSyntax);
            if (operand.Type == TypeSymbol.Error)
            {
                return new BoundErrorExpression(ownerSyntax);
            }

            var receiverType = operand.Type;

            // 接收者约束：类（含接口变量）/string/null 字面量之外拒绝
            if (receiverType != TypeSymbol.Null && receiverType != TypeSymbol.String &&
                receiverType != TypeSymbol.Any && receiverType.ElementType == null &&
                !(receiverType is ClassTypeSymbol))
            {
                _diagnostics.ReportIsAsUnsupportedReceiver(expressionSyntax.Location, receiverType);
                return new BoundErrorExpression(ownerSyntax);
            }

            if (receiverType == TypeSymbol.Any || receiverType.ElementType != null)
            {
                _diagnostics.ReportIsAsUnsupportedReceiver(expressionSyntax.Location, receiverType);
                return new BoundErrorExpression(ownerSyntax);
            }

            // null 字面量接收者：is 恒 false / as 恒 null
            if (receiverType == TypeSymbol.Null)
            {
                return wantBool
                    ? new BoundLiteralExpression(ownerSyntax, false)
                    : new BoundLiteralExpression(ownerSyntax, null!, target);
            }

            // string 接收者：目标 string → 恒真/直通；其余恒假/null
            if (receiverType == TypeSymbol.String)
            {
                if (target == TypeSymbol.String)
                {
                    return wantBool ? new BoundLiteralExpression(ownerSyntax, true) : operand;
                }

                return FoldNeverMatch(ownerSyntax, target, wantBool);
            }

            var receiverClass = (ClassTypeSymbol)receiverType;
            if (!receiverClass.IsInterface)
            {
                // 目标在接收者继承链上（含同类）→ 每个 R 实例都是 C → 静态真/直通
                if (targetClass!.IsBaseOf(receiverClass))
                {
                    return wantBool ? new BoundLiteralExpression(ownerSyntax, true) : operand;
                }

                // 接收者为目标严格基类 → 动态判定
                if (receiverClass.IsBaseOf(targetClass!))
                {
                    return wantBool
                        ? new BoundIsExpression(ownerSyntax, operand, target)
                        : new BoundAsExpression(ownerSyntax, operand, target);
                }
            }
            else
            {
                // 接口接收者：目标实现该接口 → 动态；否则不可能
                if (targetClass!.GetAllInterfaces().Contains(receiverClass))
                {
                    return wantBool
                        ? new BoundIsExpression(ownerSyntax, operand, target)
                        : new BoundAsExpression(ownerSyntax, operand, target);
                }
            }

            // 无继承关系 → 运行时不可能命中
            return FoldNeverMatch(ownerSyntax, target, wantBool);
        }

        private BoundExpression FoldNeverMatch(ExpressionSyntax ownerSyntax, TypeSymbol targetType, bool wantBool)
        {
            return wantBool
                ? new BoundLiteralExpression(ownerSyntax, false)
                : new BoundLiteralExpression(ownerSyntax, null!, targetType);
        }

        private TypeSymbol? BindTypeClause(TypeClauseSyntax? syntax)
        {
            if (syntax == null)
            {
                return null;
            }

            if (syntax is ArrayTypeClauseSyntax arrayTypeClause)
            {
                var elementType = BindTypeClause(arrayTypeClause.ElementType);
                if (elementType == null)
                {
                    return null;
                }

                return TypeSymbol.ArrayOf(elementType);
            }

            // 函数类型（6e-M22 C3）：`(A, B) -> R` → 结构化 FunctionTypeSymbol（工厂缓存同形状同实例）
            if (syntax is FunctionTypeSyntax functionType)
            {
                var functionParameters = ImmutableArray.CreateBuilder<TypeSymbol>();
                foreach (var parameterClause in functionType.ParameterTypes)
                {
                    var parameterType = BindTypeClause(parameterClause);
                    if (parameterType == null)
                    {
                        return null;
                    }

                    functionParameters.Add(parameterType);
                }

                var boundReturnType = BindTypeClause(functionType.ReturnType);
                if (boundReturnType == null)
                {
                    return null;
                }

                return FunctionTypeSymbol.Get(functionParameters.ToImmutable(), boundReturnType);
            }

            // 泛型类型实参（6e-M20）：解析定义 → 绑定实参 → 实例化去重（约束校验在实例化期，G2）
            if (syntax is GenericTypeClauseSyntax genericTypeClause)
            {
                return BindGenericTypeClause(genericTypeClause);
            }

            var type = LookupType(syntax.Identifier.Text);
            if (type == null)
            {
                _diagnostics.ReportUndefinedType(syntax.Identifier.Location, syntax.Identifier.Text);
                return null;
            }

            if (type.IsPlaceholder128)
            {
                _diagnostics.ReportUnsupported128BitType(syntax.Identifier.Location, syntax.Identifier.Text);
                return null;
            }

            // 裸泛型定义不可作具体类型使用（`var x: List` → 须 `List<int>`）
            if (type is ClassTypeSymbol { IsGenericDefinition: true })
            {
                _diagnostics.ReportGenericDefinitionRequiresTypeArguments(syntax.Identifier.Location, type.Name);
                return null;
            }

            return type;
        }

        /// <summary>Monomorphizer 专用：泛型类型子句绑定（命中同一实例化缓存壳）。</summary>
        internal TypeSymbol? BindGenericTypeClauseForExpansion(GenericTypeClauseSyntax syntax) => BindGenericTypeClause(syntax);

        /// <summary>Monomorphizer 专用：泛型方法实参子句绑定（任意类型子句）。</summary>
        internal TypeSymbol? BindTypeClauseForExpansion(TypeClauseSyntax syntax) => BindTypeClause(syntax);

        /// <summary>
        /// 内建委托家族解析（6e-M22 C3，设计 §2.3）：编译器预合成、零 stdlib 依赖、两方言共享。
        /// `Func&lt;A1..An,R&gt;` = (A..) -&gt; R（1~16 参）；`Action&lt;A1..An&gt;` = (A..) -&gt; void（0~16 参）；
        /// `Predicate&lt;T&gt;` = (T) -&gt; bool。非家族名返回 null 回落常规查找；命中但元数/绑定失败报诊断返回 Error 壳。
        /// </summary>
        private TypeSymbol? TryResolveDelegateFamily(SyntaxToken identifier, ImmutableArray<TypeClauseSyntax> argumentClauses)
        {
            var name = identifier.Text;

            if (name != "Func" && name != "Action" && name != "Predicate")
            {
                return null;
            }

            switch (name)
            {
                case "Func":
                    if (argumentClauses.Length < 2 || argumentClauses.Length > 17)
                    {
                        _diagnostics.ReportError(identifier.Location, $"Func 需要 2~17 个类型实参（末位为返回类型，至多 16 个参数），实际 {argumentClauses.Length} 个。");
                        return TypeSymbol.Error;
                    }

                    return BindDelegateFamilyShape(argumentClauses, returnTypeFromLastArgument: true);

                case "Action":
                    if (argumentClauses.Length > 16)
                    {
                        _diagnostics.ReportError(identifier.Location, $"Action 至多 16 个类型实参，实际 {argumentClauses.Length} 个。");
                        return TypeSymbol.Error;
                    }

                    return BindDelegateFamilyShape(argumentClauses, returnTypeFromLastArgument: false);

                default: // Predicate
                    if (argumentClauses.Length != 1)
                    {
                        _diagnostics.ReportError(identifier.Location, $"Predicate 需要恰好 1 个类型实参，实际 {argumentClauses.Length} 个。");
                        return TypeSymbol.Error;
                    }

                    var predicateParameter = BindTypeClauseForExpansion(argumentClauses[0]);
                    if (predicateParameter == null)
                    {
                        return TypeSymbol.Error;
                    }

                    return FunctionTypeSymbol.Get(ImmutableArray.Create(predicateParameter), TypeSymbol.Boolean);
            }
        }

        /// <summary>家族形状绑定：逐实参绑定 → Func 取末位为返回类型 / Action 返回 void。</summary>
        private TypeSymbol? BindDelegateFamilyShape(ImmutableArray<TypeClauseSyntax> argumentClauses, bool returnTypeFromLastArgument)
        {
            var parameterCount = returnTypeFromLastArgument ? argumentClauses.Length - 1 : argumentClauses.Length;
            var parameters = ImmutableArray.CreateBuilder<TypeSymbol>(parameterCount);

            for (var i = 0; i < parameterCount; i++)
            {
                var parameterType = BindTypeClauseForExpansion(argumentClauses[i]);
                if (parameterType == null)
                {
                    return TypeSymbol.Error;
                }

                parameters.Add(parameterType);
            }

            if (!returnTypeFromLastArgument)
            {
                return FunctionTypeSymbol.Get(parameters.ToImmutable(), TypeSymbol.Void);
            }

            var returnType = BindTypeClauseForExpansion(argumentClauses[^1]);
            if (returnType == null)
            {
                return TypeSymbol.Error;
            }

            return FunctionTypeSymbol.Get(parameters.ToImmutable(), returnType);
        }

        /// <summary>Monomorphizer 专用：泛型类型名绑定（new/调用站点的 Identifier+实参列表，命中同一缓存壳）。</summary>
        internal TypeSymbol? BindGenericTypeNameForExpansion(SyntaxToken identifier, ImmutableArray<TypeClauseSyntax> argumentClauses) => BindGenericTypeName(identifier, argumentClauses);

        /// <summary>
        /// 泛型类型子句绑定（6e-M20）：`List&lt;int&gt;` / 嵌套 `List&lt;List&lt;int&gt;&gt;` → 泛型名解析核心。
        /// </summary>
        private TypeSymbol? BindGenericTypeClause(GenericTypeClauseSyntax syntax)
        {
            return BindGenericTypeName(syntax.Identifier, syntax.TypeArguments);
        }

        /// <summary>
        /// 泛型类型名绑定核心（6e-M20）：名字 + 实参列表 → 定义查找/非泛型拒绝/元数校验/约束校验/实例化去重。
        /// 类型子句与 `new Box&lt;int&gt;(…)` 两路共用。
        /// </summary>
        private TypeSymbol? BindGenericTypeName(SyntaxToken identifier, ImmutableArray<TypeClauseSyntax> argumentClauses)
        {
            // 内建委托家族（6e-M22 C3）：Func<…>/Action<…>/Predicate<T> → 结构化函数类型（两方言共享拼写）
            var familyResult = TryResolveDelegateFamily(identifier, argumentClauses);
            if (familyResult != null)
            {
                return familyResult;
            }

            var definition = LookupType(identifier.Text) as ClassTypeSymbol;
            if (definition == null)
            {
                _diagnostics.ReportUndefinedType(identifier.Location, identifier.Text);
                return null;
            }

            if (!definition.IsGenericDefinition)
            {
                _diagnostics.ReportError(identifier.Location, $"'{definition.Name}' 不是泛型类型，不能带类型实参。");
                return null;
            }

            var arguments = ImmutableArray.CreateBuilder<TypeSymbol>();
            foreach (var argumentSyntax in argumentClauses)
            {
                var argument = BindTypeClause(argumentSyntax);
                if (argument == null)
                {
                    return null;
                }

                arguments.Add(argument);
            }

            if (arguments.Count != definition.TypeParameters.Length)
            {
                _diagnostics.ReportError(identifier.Location, $"泛型类型 '{definition.Name}' 需要 {definition.TypeParameters.Length} 个类型实参，但提供了 {arguments.Count} 个。");
                return null;
            }

            ValidateTypeArgumentConstraints(identifier.Location, definition, arguments.ToImmutable());

            return GenericTypeInstantiator.Instantiate(definition, arguments.ToImmutable());
        }

        private BoundExpression BindGenericMethodCall(CallExpressionSyntax syntax)
        {
            var identifier = syntax.Identifier.Text;
            var errorLocation = syntax.TypeArguments!.Location;
            var definition = ResolveGenericMethodDefinition(identifier, syntax.TypeArguments.Arguments.Length, errorLocation);

            if (definition == null)
            {
                return new BoundErrorExpression(syntax);
            }

            var instantiated = InstantiateGenericMethod(errorLocation, identifier, definition, syntax.TypeArguments.Arguments, syntax.Arguments.Count);
            if (instantiated == null)
            {
                return new BoundErrorExpression(syntax);
            }

            return new BoundCallExpression(syntax, instantiated, BindGenericMethodArguments(syntax.Arguments, instantiated));
        }

        /// <summary>
        /// 成员/类静态泛型方法显式实参调用（6e-M22 C1）：list.Pick&lt;T&gt;(…) / Json.Swap&lt;T&gt;(…) / MyNs.Swap&lt;T&gt;(…)。
        /// 三路与 <see cref="BindMemberCallExpression"/> 同优先级：命名空间/别名限定函数 → 类静态泛型 → 实例接收者泛型；
        /// 恰一候选规则与顶层路径一致。
        /// </summary>
        private BoundExpression BindGenericMemberMethodCall(MemberCallExpressionSyntax syntax)
        {
            var identifier = syntax.IdentifierToken.Text;
            var errorLocation = syntax.TypeArguments!.Location;
            var arity = syntax.TypeArguments.Arguments.Length;

            // 路径一：命名空间/using 别名限定函数（先于类型名，避免 .NET 真实类型劫持——与普通成员调用同序）
            var prefix = ResolveDottedTypeName(syntax.Expression);
            if (!string.IsNullOrEmpty(prefix))
            {
                var candidates = ResolveDottedFunctionCandidates(prefix!, identifier);
                if (candidates != null)
                {
                    var matches = candidates.Value
                        .Where(f => f.IsGenericMethod && f.TypeParameters.Length == arity)
                        .ToImmutableArray();

                    if (matches.Length == 0)
                    {
                        _diagnostics.ReportError(errorLocation, $"找不到接受 {arity} 个类型实参的泛型函数 '{prefix}.{identifier}'。");
                        return new BoundErrorExpression(syntax);
                    }

                    if (matches.Length > 1)
                    {
                        _diagnostics.ReportError(errorLocation, $"泛型函数 '{prefix}.{identifier}' 调用歧义。");
                        return new BoundErrorExpression(syntax);
                    }

                    var nsInstantiated = InstantiateGenericMethod(errorLocation, identifier, matches[0], syntax.TypeArguments.Arguments, syntax.Arguments.Count);
                    if (nsInstantiated == null)
                    {
                        return new BoundErrorExpression(syntax);
                    }

                    return new BoundCallExpression(syntax, nsInstantiated, BindGenericMethodArguments(syntax.Arguments, nsInstantiated));
                }
            }

            // 路径二：类静态泛型方法（点号目标解析为类型名）
            if (!string.IsNullOrEmpty(prefix) && LookupType(prefix!) is ClassTypeSymbol staticType)
            {
                if (staticType.IsGenericDefinition)
                {
                    _diagnostics.ReportError(errorLocation, $"泛型定义 '{staticType.FullName}' 的静态成员须经实例化访问，如 '{staticType.Name}<int>.{identifier}<…>(…)'。");
                    return new BoundErrorExpression(syntax);
                }

                var staticMatches = staticType.GetMethods(identifier)
                    .Where(m => m.IsStatic && m.IsGenericMethod && m.TypeParameters.Length == arity && IsAccessibleMember(m.Visibility, staticType))
                    .ToImmutableArray();

                if (staticMatches.Length == 0)
                {
                    _diagnostics.ReportError(errorLocation, $"类型 '{staticType.FullName}' 上找不到接受 {arity} 个类型实参的静态泛型方法 '{identifier}'。");
                    return new BoundErrorExpression(syntax);
                }

                if (staticMatches.Length > 1)
                {
                    _diagnostics.ReportError(errorLocation, $"静态泛型方法 '{staticType.FullName}.{identifier}' 调用歧义。");
                    return new BoundErrorExpression(syntax);
                }

                var staticInstantiated = InstantiateGenericMethod(errorLocation, identifier, staticMatches[0], syntax.TypeArguments.Arguments, syntax.Arguments.Count);
                if (staticInstantiated == null)
                {
                    return new BoundErrorExpression(syntax);
                }

                return new BoundMemberCallExpression(
                    syntax,
                    new BoundStaticTypeExpression(syntax.Expression, staticType),
                    identifier,
                    BindGenericMethodArguments(syntax.Arguments, staticInstantiated),
                    staticInstantiated.ReturnType,
                    staticInstantiated);
            }

            // 路径三：实例接收者泛型方法（receiver 类型上的模板；泛型类经实例化携带，见 SubstituteMethod）
            var boundReceiver = BindExpression(syntax.Expression);
            if (boundReceiver.Type == TypeSymbol.Error)
            {
                return new BoundErrorExpression(syntax);
            }

            if (boundReceiver.Type is ClassTypeSymbol receiverClass)
            {
                var instanceMatches = receiverClass.GetMethods(identifier)
                    .Where(m => !m.IsStatic && m.IsGenericMethod && m.TypeParameters.Length == arity && IsAccessibleMember(m.Visibility, receiverClass))
                    .ToImmutableArray();

                if (instanceMatches.Length == 0)
                {
                    _diagnostics.ReportError(errorLocation, $"类型 '{receiverClass}' 上找不到接受 {arity} 个类型实参的实例泛型方法 '{identifier}'。");
                    return new BoundErrorExpression(syntax);
                }

                if (instanceMatches.Length > 1)
                {
                    _diagnostics.ReportError(errorLocation, $"实例泛型方法 '{receiverClass}.{identifier}' 调用歧义。");
                    return new BoundErrorExpression(syntax);
                }

                var instanceInstantiated = InstantiateGenericMethod(errorLocation, identifier, instanceMatches[0], syntax.TypeArguments.Arguments, syntax.Arguments.Count);
                if (instanceInstantiated == null)
                {
                    return new BoundErrorExpression(syntax);
                }

                var isBase = boundReceiver is BoundBaseExpression;

                return new BoundMemberCallExpression(
                    syntax,
                    boundReceiver,
                    identifier,
                    BindGenericMethodArguments(syntax.Arguments, instanceInstantiated),
                    instanceInstantiated.ReturnType,
                    instanceInstantiated,
                    isBase);
            }

            _diagnostics.ReportNotAFunction(syntax.IdentifierToken.Location, identifier);
            return new BoundErrorExpression(syntax);
        }

        /// <summary>泛型方法调用共享核心：类型实参绑定 → 实例化期约束校验 → 缓存实例化 → 元数校验。失败返回 null（诊断已报）。</summary>
        private FunctionSymbol? InstantiateGenericMethod(TextLocation errorLocation, string displayName, FunctionSymbol definition, ImmutableArray<TypeClauseSyntax> typeArgumentClauses, int argumentCount)
        {
            var arguments = ImmutableArray.CreateBuilder<TypeSymbol>();
            foreach (var clause in typeArgumentClauses)
            {
                var argument = BindTypeClause(clause);
                if (argument == null)
                {
                    return null;
                }

                arguments.Add(argument);
            }

            ValidateTypeArgumentConstraints(errorLocation, definition.TypeParameters, arguments.ToImmutable(), displayName);

            var instantiated = GenericMethodInstantiator.Instantiate(definition, arguments.ToImmutable());

            if (instantiated.Parameters.Length != argumentCount)
            {
                _diagnostics.ReportWrongArgumentCount(errorLocation, displayName, instantiated.Parameters.Length, argumentCount);
                return null;
            }

            return instantiated;
        }

        /// <summary>泛型方法实参转换绑定（元数已由共享核心校验）。</summary>
        private ImmutableArray<BoundExpression> BindGenericMethodArguments(SeparatedSyntaxList<ExpressionSyntax> argumentSyntaxes, FunctionSymbol instantiated)
        {
            var boundArguments = ImmutableArray.CreateBuilder<BoundExpression>();
            for (var i = 0; i < argumentSyntaxes.Count; i++)
            {
                boundArguments.Add(BindConversion(argumentSyntaxes[i].Location, BindExpression(argumentSyntaxes[i]), instantiated.Parameters[i].Type));
            }

            return boundArguments.ToImmutable();
        }

        /// <summary>泛型方法定义解析：裸函数 → using 命名空间函数；元数须恰一候选。</summary>
        private FunctionSymbol? ResolveGenericMethodDefinition(string name, int typeArgumentCount, TextLocation errorLocation)
        {
            var candidates = _scope.TryLookupFunctions(name);

            if (candidates == null)
            {
                foreach (var ns in _usingNamespaces)
                {
                    var usingCandidates = _scope.TryLookupNamespaceFunctions(ns, name);
                    if (usingCandidates != null)
                    {
                        candidates = usingCandidates;
                        break;
                    }
                }
            }

            if (candidates == null || candidates.Value.Length == 0)
            {
                _diagnostics.ReportUndefinedFunction(errorLocation, name);
                return null;
            }

            var matches = candidates.Value.Where(f => f.IsGenericMethod && f.TypeParameters.Length == typeArgumentCount).ToImmutableArray();

            if (matches.Length == 0)
            {
                _diagnostics.ReportError(errorLocation, $"找不到接受 {typeArgumentCount} 个类型实参的泛型方法 '{name}'。");
                return null;
            }

            if (matches.Length > 1)
            {
                _diagnostics.ReportError(errorLocation, $"泛型方法 '{name}' 调用歧义。");
                return null;
            }

            return matches[0];
        }

        /// <summary>Monomorphizer 专用：泛型方法定义解析（裸函数 + 命名空间；命中同一实例化缓存）。</summary>
        internal FunctionSymbol? ResolveGenericMethodDefinitionForExpansion(string name, int typeArgumentCount)
        {
            var candidates = _scope.TryLookupFunctions(name);

            if (candidates == null)
            {
                foreach (var ns in _usingNamespaces)
                {
                    var usingCandidates = _scope.TryLookupNamespaceFunctions(ns, name);
                    if (usingCandidates != null)
                    {
                        candidates = usingCandidates;
                        break;
                    }
                }
            }

            if (candidates == null)
            {
                return null;
            }

            var matches = candidates.Value.Where(f => f.IsGenericMethod && f.TypeParameters.Length == typeArgumentCount).ToImmutableArray();

            return matches.Length == 1 ? matches[0] : null;
        }

        /// <summary>
        /// 实例化期约束校验（6e-M20）：实参须满足 where 约束（引用类型/接口/基类）。
        /// 类型参数作实参（嵌套上下文）暂跳过——由 Monomorphizer 展开时按外层映射判定。
        /// </summary>
        private void ValidateTypeArgumentConstraints(TextLocation errorLocation, ClassTypeSymbol definition, ImmutableArray<TypeSymbol> arguments)
            => ValidateTypeArgumentConstraints(errorLocation, definition.TypeParameters, arguments, definition.Name);

        /// <summary>类/方法两路共用的约束校验核心。</summary>
        private void ValidateTypeArgumentConstraints(TextLocation errorLocation, ImmutableArray<TypeParameterSymbol> typeParameters, ImmutableArray<TypeSymbol> arguments, string definitionName)
        {
            for (var i = 0; i < typeParameters.Length && i < arguments.Length; i++)
            {
                var parameter = typeParameters[i];
                var argument = arguments[i];

                if (argument is TypeParameterSymbol)
                {
                    continue;
                }

                if (parameter.HasReferenceTypeConstraint && !IsReferenceType(argument))
                {
                    _diagnostics.ReportError(errorLocation, $"泛型 '{definitionName}' 的类型参数 '{parameter.Name}' 要求引用类型（where {parameter.Name}: class），但实参 '{argument.Name}' 是值类型。");
                    continue;
                }

                // struct 值类型约束（6e-M22 C1）：基元数值/bool/char + enum
                if (parameter.HasValueTypeConstraint && !IsValueType(argument))
                {
                    _diagnostics.ReportError(errorLocation, $"泛型 '{definitionName}' 的类型参数 '{parameter.Name}' 要求值类型（where {parameter.Name}: struct），但实参 '{argument.Name}' 不是值类型。");
                    continue;
                }

                foreach (var constraint in parameter.ConstraintTypes)
                {
                    if (constraint is not ClassTypeSymbol constraintClass)
                    {
                        _diagnostics.ReportError(errorLocation, $"约束 '{constraint.Name}' 不是受支持的约束形式（支持接口/基类）。");
                        continue;
                    }

                    var constraintName = constraintClass.FullName;

                    if (argument is not ClassTypeSymbol argumentClass)
                    {
                        _diagnostics.ReportError(errorLocation, $"泛型 '{definitionName}' 的类型实参 '{argument.Name}' 不满足约束 '{constraintName}'。");
                        continue;
                    }

                    var satisfied = constraintClass.IsInterface
                        ? argumentClass.GetAllInterfaces().Contains(constraintClass) || argumentClass == constraintClass
                        : constraintClass.IsBaseOf(argumentClass);

                    if (!satisfied)
                    {
                        _diagnostics.ReportError(errorLocation, $"泛型 '{definitionName}' 的类型实参 '{argument.Name}' 不满足约束 '{constraintName}'（where {parameter.Name}: {constraintName}）。");
                    }
                }
            }
        }

        /// <summary>引用类型判定（where T: class）：类/接口/string/数组；基元值类型为否。</summary>
        private static bool IsReferenceType(TypeSymbol type)
        {
            if (type is ClassTypeSymbol || type is TypeParameterSymbol)
            {
                return true;
            }

            if (type.ElementType != null && type.Kind == SymbolKind.Type)
            {
                return true;
            }

            return type == TypeSymbol.String || type == TypeSymbol.Any;
        }

        /// <summary>值类型判定（where T: struct，6e-M22 C1）：基元数值全集 + bool + char + enum；语言暂无用户 struct。</summary>
        private static bool IsValueType(TypeSymbol type)
        {
            if (type is EnumTypeSymbol)
            {
                return true;
            }

            return type == TypeSymbol.Int8 || type == TypeSymbol.UInt8
                || type == TypeSymbol.Int16 || type == TypeSymbol.UInt16
                || type == TypeSymbol.Int32 || type == TypeSymbol.UInt32
                || type == TypeSymbol.Int64 || type == TypeSymbol.UInt64
                || type == TypeSymbol.Float || type == TypeSymbol.Double
                || type == TypeSymbol.Boolean || type == TypeSymbol.Char;
        }

        private BoundStatement BindIfStatement(IfStatementSyntax syntax)
        {
            var condition = BindExpression(syntax.Condition, TypeSymbol.Boolean);

            if (condition.ConstantValue != null)
            {
                if ((bool)condition.ConstantValue.Value == false)
                {
                    _diagnostics.ReportUnreachableCode(syntax.ThenStatement);
                }
                else if (syntax.ElseClause != null)
                {
                    _diagnostics.ReportUnreachableCode(syntax.ElseClause.ElseStatement);
                }
            }

            var thenStatement = BindStatement(syntax.ThenStatement);
            var elseStatement = syntax.ElseClause == null ? null : BindStatement(syntax.ElseClause.ElseStatement);

            return new BoundIfStatement(syntax, condition, thenStatement, elseStatement);
        }

        private BoundStatement BindWhileStatement(WhileStatementSyntax syntax)
        {
            var condition = BindExpression(syntax.Condition, TypeSymbol.Boolean);

            if (condition.ConstantValue != null)
            {
                if (!(bool)condition.ConstantValue.Value)
                {
                    _diagnostics.ReportUnreachableCode(syntax.Body);
                }
            }

            var body = BindLoopBody(syntax.Body, out var breakLabel, out var continueLabel);

            return new BoundWhileStatement(syntax, condition, body, breakLabel, continueLabel);
        }

        private BoundStatement BindDoWhileStatement(DoWhileStatementSyntax syntax)
        {
            var body = BindLoopBody(syntax.Body, out var breakLabel, out var continueLabel);
            var condition = BindExpression(syntax.Condition, TypeSymbol.Boolean);

            return new BoundDoWhileStatement(syntax, body, condition, breakLabel, continueLabel);
        }

        private BoundStatement BindForStatement(ForStatementSyntax syntax)
        {
            var lowerBound = BindExpression(syntax.LowerBound, TypeSymbol.Int32);
            var upperBound = BindExpression(syntax.UpperBound, TypeSymbol.Int32);

            // 可选步长：V1 仅支持常量正整数（负步长会破坏 `i <= upper` 条件语义）
            BoundExpression? step = null;
            if (syntax.Step != null)
            {
                step = BindExpression(syntax.Step, TypeSymbol.Int32);
                if (step.ConstantValue == null ||
                    step.ConstantValue.Value is not int stepValue ||
                    stepValue <= 0)
                {
                    _diagnostics.ReportError(syntax.Step.Location, "for 循环的 step 必须为常量正整数。");
                }
            }

            _scope = new BoundScope(_scope);

            VariableSymbol variable;

            if (syntax.Identifier != null)
            {
                if (syntax.VarKeyword != null)
                {
                    // var → 声明新的可变循环变量
                    variable = BindVariableDeclaration(syntax.Identifier, isReadOnly: false, TypeSymbol.Int32);
                }
                else
                {
                    // 无关键字 → 复用外层已存在变量（必须已声明且可变）
                    var lookup = _scope.TryLookupSymbol(syntax.Identifier.Text);
                    if (lookup is VariableSymbol existingVariable)
                    {
                        if (existingVariable.IsReadOnly)
                        {
                            _diagnostics.ReportError(syntax.Identifier.Location, $"循环变量 '{existingVariable.Name}' 是只读的，for 循环需要可写变量。");
                        }

                        variable = existingVariable;
                    }
                    else
                    {
                        _diagnostics.ReportError(syntax.Identifier.Location, $"循环变量 '{syntax.Identifier.Text}' 未定义。省略 var 时循环变量必须在外部作用域已声明。");
                        variable = BindVariableDeclaration(syntax.Identifier, isReadOnly: true, TypeSymbol.Int32);
                    }
                }
            }
            else
            {
                // 纯次数循环 for (1 to 10)：隐藏计数器（不进作用域查找，用户不可见）
                variable = new LocalVariableSymbol("__for", isReadOnly: true, TypeSymbol.Int32, constant: null);
            }

            var body = BindLoopBody(syntax.Body, out var breakLabel, out var continueLabel);

            _scope = _scope.Parent!;

            return new BoundForStatement(syntax, variable, lowerBound, upperBound, step, body, breakLabel, continueLabel);
        }

        /// <summary>foreach 绑定期脱糖为 while 索引循环（策略点：v1 数组/字符串）：</summary>
        /// <remarks>
        /// {
        ///     var __i = 0
        ///     while (__i &lt; collection.Length)
        ///     {
        ///         var x = collection[__i]     // 内层作用域，每迭代新只读变量
        ///         body
        ///         continue:
        ///         __i++
        ///     }
        /// }
        /// </remarks>
        private BoundStatement BindForeachStatement(ForeachStatementSyntax syntax)
        {
            var collection = BindExpression(syntax.Collection);

            TypeSymbol elementType;
            if (collection.Type.ElementType != null)
            {
                elementType = collection.Type.ElementType;
            }
            else if (collection.Type == TypeSymbol.String)
            {
                elementType = TypeSymbol.Char;
            }
            else
            {
                // 6e-M20 G6 枚举器模式：集合实现 System.Collections.Generic.IEnumerable<T> →
                // GetEnumerator()/MoveNext()/Current 降级循环（数组/string 保持索引路径）。
                // 方法解析走具体枚举器类（GetEnumerator 返回类型），接口仅作编译期能力标记——native 免接口分派。
                var enumeratorClass = FindEnumeratorClass(collection.Type);
                if (enumeratorClass != null)
                {
                    return BindEnumeratorForeach(syntax, collection, enumeratorClass);
                }

                elementType = TypeSymbol.Error;
                _diagnostics.ReportError(syntax.Collection.Location, $"foreach 只能遍历数组、字符串或实现 IEnumerable<T> 的集合，不能遍历 '{collection.Type}'。");
            }

            _scope = new BoundScope(_scope);

            // 隐藏计数器 __i（唯一名，用户不可见）
            _labelCounter++;
            var counterName = $"__foreach_i{_labelCounter}";
            var counterToken = new SyntaxToken(syntax.SyntaxTree, SyntaxKind.IdentifierToken, syntax.Keyword.Span.Start, counterName, counterName, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var counter = BindVariableDeclaration(counterToken, isReadOnly: false, TypeSymbol.Int32);

            var breakLabel = new BoundLabel($"break{_labelCounter}");
            var continueLabel = new BoundLabel($"continue{_labelCounter}");
            var whileContinueLabel = new BoundLabel($"whilecontinue{_labelCounter}");

            var loopBody = ImmutableArray.CreateBuilder<BoundStatement>();

            // 内层作用域：循环变量 x（只读，每迭代新建，C# 语义）
            _scope = new BoundScope(_scope);
            var loopVar = BindVariableDeclaration(syntax.Identifier, isReadOnly: true, elementType);
            var elementAccess = new BoundElementAccessExpression(syntax, elementType, collection, BoundNodeFactory.Variable(syntax, counter));
            loopBody.Add(BoundNodeFactory.VariableDeclaration(syntax, loopVar, elementAccess));

            _loopStack.Push((breakLabel, continueLabel));
            loopBody.Add(BindStatement(syntax.Body));
            _loopStack.Pop();

            _scope = _scope.Parent!;

            loopBody.Add(BoundNodeFactory.Label(syntax, continueLabel));
            loopBody.Add(BoundNodeFactory.Increment(syntax, BoundNodeFactory.Variable(syntax, counter)));

            var lengthAccess = new BoundMemberAccessExpression(syntax, TypeSymbol.Int32, collection, "Length");
            var condition = BoundNodeFactory.Binary(syntax,
                BoundNodeFactory.Variable(syntax, counter),
                SyntaxKind.LessToken,
                lengthAccess);

            var whileStatement = BoundNodeFactory.While(syntax, condition,
                new BoundBlockStatement(syntax, loopBody.ToImmutable()), breakLabel, whileContinueLabel);

            var counterInit = BoundNodeFactory.VariableDeclaration(syntax, counter, BoundNodeFactory.Literal(syntax, 0));

            _scope = _scope.Parent!;

            return BoundNodeFactory.Block(syntax, counterInit, whileStatement);
        }

        /// <summary>
        /// 集合是否可枚举（6e-M20 G6）：实现 System.Collections.Generic.IEnumerable&lt;T&gt; 实例化
        /// 且存在无参 GetEnumerator() 方法 → 返回其具体枚举器类。
        /// </summary>
        private static ClassTypeSymbol? FindEnumeratorClass(TypeSymbol collectionType)
        {
            if (collectionType is not ClassTypeSymbol classType || classType.IsInterface)
            {
                return null;
            }

            var getEnumerator = classType.GetMethod("GetEnumerator");
            if (getEnumerator == null || getEnumerator.Parameters.Length > 0)
            {
                return null;
            }

            // 直接模式：GetEnumerator 返回具备 MoveNext() 与 Current 的类或接口即视为可枚举
            // （无需显式声明 IEnumerable<T>，支持 CO/BCL 自定义枚举器，如 List<T>）。
            if (getEnumerator.ReturnType is ClassTypeSymbol enumType &&
                enumType.GetMethod("MoveNext") != null &&
                enumType.GetProperty("Current")?.Getter != null)
            {
                return enumType;
            }

            // 传统判定：实现 System.Collections.Generic.IEnumerable<T>。
            foreach (var iface in classType.GetAllInterfaces())
            {
                if (iface is InstantiatedTypeSymbol instantiated &&
                    instantiated.GenericDefinition.Name == "IEnumerable" &&
                    instantiated.GenericDefinition.Namespace == "System.Collections.Generic")
                {
                    return getEnumerator.ReturnType as ClassTypeSymbol;
                }
            }

            return null;
        }

        /// <summary>
        /// foreach 枚举器降级（6e-M20 G6，P6 策略点兑现）：
        /// var __enum = collection.GetEnumerator()
        /// while __enum.MoveNext()
        /// {
        ///     var x = __enum.Current   // 只读局部，每迭代新建
        ///     body
        /// }
        /// </summary>
        private BoundStatement BindEnumeratorForeach(ForeachStatementSyntax syntax, BoundExpression collection, ClassTypeSymbol enumeratorClass)
        {
            _labelCounter++;
            var counter = _labelCounter;

            var moveNextMethod = enumeratorClass.GetMethod("MoveNext");
            var currentProperty = enumeratorClass.GetProperty("Current");

            if (moveNextMethod == null || currentProperty?.Getter == null)
            {
                _diagnostics.ReportError(syntax.Collection.Location, $"枚举器类型 '{enumeratorClass.Name}' 须实现 MoveNext() 与 Current。");
                return new BoundBlockStatement(syntax, ImmutableArray<BoundStatement>.Empty);
            }

            var elementType = currentProperty.Type;

            _scope = new BoundScope(_scope);

            // 隐藏枚举器变量 __enum
            var enumToken = new SyntaxToken(syntax.SyntaxTree, SyntaxKind.IdentifierToken, syntax.Keyword.Span.Start, $"__foreach_e{counter}", $"__foreach_e{counter}", ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
            var enumeratorDecl = BindVariableDeclaration(enumToken, isReadOnly: false, enumeratorClass);

            var breakLabel = new BoundLabel($"break{counter}");
            var continueLabel = new BoundLabel($"continue{counter}");
            var whileContinueLabel = new BoundLabel($"whilecontinue{counter}");

            var enumVariable = BoundNodeFactory.Variable(syntax, enumeratorDecl);

            // init：collection.GetEnumerator()
            var getEnumeratorMethod = ((ClassTypeSymbol)collection.Type).GetMethod("GetEnumerator")!;
            var getEnumeratorCall = new BoundMemberCallExpression(syntax, collection, "GetEnumerator", ImmutableArray<BoundExpression>.Empty, enumeratorClass, getEnumeratorMethod);
            var enumeratorInit = BoundNodeFactory.VariableDeclaration(syntax, enumeratorDecl, getEnumeratorCall);

            // 内层作用域：循环变量 x（只读，每迭代新建，C# 语义）
            _scope = new BoundScope(_scope);
            var loopVar = BindVariableDeclaration(syntax.Identifier, isReadOnly: true, elementType);

            var loopBody = ImmutableArray.CreateBuilder<BoundStatement>();
            var currentRead = new BoundMemberCallExpression(syntax, enumVariable, "Current", ImmutableArray<BoundExpression>.Empty, elementType, currentProperty.Getter);
            loopBody.Add(BoundNodeFactory.VariableDeclaration(syntax, loopVar, currentRead));

            _loopStack.Push((breakLabel, continueLabel));
            loopBody.Add(BindStatement(syntax.Body));
            _loopStack.Pop();

            _scope = _scope.Parent!;

            // 条件：__enum.MoveNext()
            var condition = new BoundMemberCallExpression(syntax, enumVariable, "MoveNext", ImmutableArray<BoundExpression>.Empty, TypeSymbol.Boolean, moveNextMethod);

            var whileStatement = BoundNodeFactory.While(syntax, condition,
                new BoundBlockStatement(syntax, loopBody.ToImmutable()), breakLabel, whileContinueLabel);

            _scope = _scope.Parent!;

            return BoundNodeFactory.Block(syntax, enumeratorInit, whileStatement);
        }

        /// <summary>switch 绑定期降级为嵌套 if-else 链（不支持 fall-through）：</summary>
        /// <remarks>
        /// if (value == c1 && when) { body1 } else if (value == c2) { body2 } else { default }
        /// switchend:   // 节内 break → goto 此处
        /// </remarks>
        private BoundStatement BindSwitchStatement(SwitchStatementSyntax syntax)
        {
            var value = BindExpression(syntax.Expression);

            _labelCounter++;
            var switchEndLabel = new BoundLabel($"switchend{_labelCounter}");

            var defaultCount = 0;
            foreach (var section in syntax.Sections)
            {
                if (section is DefaultClauseSyntax)
                {
                    defaultCount++;
                }
            }

            if (defaultCount > 1)
            {
                _diagnostics.ReportError(syntax.Keyword.Location, "switch 不能有多个 default 子句。");
            }

            // 按源顺序绑定各节（诊断顺序稳定）：空体 case（叠标）把值合并进下一个非空节
            var conditions = ImmutableArray.CreateBuilder<BoundExpression?>();
            var bodies = ImmutableArray.CreateBuilder<BoundStatement>();

            var pendingValues = ImmutableArray.CreateBuilder<BoundExpression>();

            foreach (var section in syntax.Sections)
            {
                if (section is DefaultClauseSyntax defaultClause)
                {
                    var defaultBodySyntax = defaultClause.Body;
                    ReportSwitchFallThrough(defaultBodySyntax);

                    _loopStack.Push((switchEndLabel, null));
                    var defaultBody = BindStatement(defaultBodySyntax);
                    _loopStack.Pop();

                    conditions.Add(null);
                    bodies.Add(defaultBody);
                    continue;
                }

                var caseClause = (CaseClauseSyntax)section;
                var clauseValues = ImmutableArray.CreateBuilder<BoundExpression>();
                foreach (var valueSyntax in caseClause.Values)
                {
                    var caseValue = BindExpression(valueSyntax);
                    if (caseValue.ConstantValue == null && caseValue.Type != TypeSymbol.Error)
                    {
                        _diagnostics.ReportError(valueSyntax.Location, "case 值必须是常量。");
                    }

                    clauseValues.Add(caseValue);
                }

                var isStackedLabel = caseClause.Body is BlockStatementSyntax emptyBlock && emptyBlock.Statements.Length == 0;
                if (isStackedLabel)
                {
                    pendingValues.AddRange(clauseValues);
                    continue;
                }

                // 非空节：合并之前叠标的值 + 本节的 when
                BoundExpression? condition = null;
                var allValues = pendingValues.ToImmutable().AddRange(clauseValues);
                foreach (var caseValue in allValues)
                {
                    var equality = BoundNodeFactory.Binary(syntax, value, SyntaxKind.EqualsEqualsToken, caseValue);
                    condition = condition == null
                        ? equality
                        : BoundNodeFactory.Binary(syntax, condition, SyntaxKind.PipePipeToken, equality);
                }

                pendingValues.Clear();

                if (caseClause.WhenCondition != null)
                {
                    var whenCondition = BindExpression(caseClause.WhenCondition, TypeSymbol.Boolean);
                    condition = condition == null
                        ? whenCondition
                        : BoundNodeFactory.Binary(syntax, condition, SyntaxKind.AmpersandAmpersandToken, whenCondition);
                }

                var bodySyntax = caseClause.Body;
                ReportSwitchFallThrough(bodySyntax);

                _loopStack.Push((switchEndLabel, null));
                var body = BindStatement(bodySyntax);
                _loopStack.Pop();

                conditions.Add(condition);
                bodies.Add(body);
            }

            // 末尾叠标（最后一个 case 体为空）：合并为一个空条件节，值匹配后无操作
            if (pendingValues.Count > 0)
            {
                BoundExpression? trailingCondition = null;
                foreach (var caseValue in pendingValues)
                {
                    var equality = BoundNodeFactory.Binary(syntax, value, SyntaxKind.EqualsEqualsToken, caseValue);
                    trailingCondition = trailingCondition == null
                        ? equality
                        : BoundNodeFactory.Binary(syntax, trailingCondition, SyntaxKind.PipePipeToken, equality);
                }

                conditions.Add(trailingCondition);
                bodies.Add(BoundNodeFactory.Block(syntax));
            }

            // 反向构建 if-else 链（首个 case 为最外层，保持源顺序）
            BoundStatement? chain = null;
            for (var i = conditions.Count - 1; i >= 0; i--)
            {
                var condition = conditions[i];
                chain = condition == null
                    ? new BoundIfStatement(syntax, BoundNodeFactory.Literal(syntax, true), bodies[i], chain)
                    : new BoundIfStatement(syntax, condition, bodies[i], chain);
            }

            var result = ImmutableArray.CreateBuilder<BoundStatement>();
            if (chain != null)
            {
                result.Add(chain);
            }

            result.Add(BoundNodeFactory.Label(syntax, switchEndLabel));

            return BoundNodeFactory.Block(syntax, result.ToArray());
        }

        /// <summary>不支持 fall-through：非空节体末尾必须 break/return/continue（叠标空体除外）。</summary>
        private void ReportSwitchFallThrough(StatementSyntax body)
        {
            var last = body is BlockStatementSyntax block
                ? (block.Statements.Length > 0 ? block.Statements[^1] : null)
                : body;

            if (last == null)
            {
                return;
            }

            if (last.Kind is SyntaxKind.BreakStatement or SyntaxKind.ReturnStatement or SyntaxKind.ContinueStatement)
            {
                return;
            }

            _diagnostics.ReportError(last.Location, "switch 节体必须以 break/return/continue 结尾（不支持 fall-through）。");
        }

        private BoundStatement BindCSStyleForStatement(CSStyleForStatementSyntax syntax)
        {
            _scope = new BoundScope(_scope);

            BoundStatement? init = null;
            if (syntax.Init != null)
            {
                init = BindStatement(syntax.Init);
            }

            var condition = syntax.Condition == null ? null : BindExpression(syntax.Condition, TypeSymbol.Boolean);
            var update = syntax.Update == null ? null : BindExpression(syntax.Update);
            var body = BindLoopBody(syntax.Body, out var breakLabel, out var continueLabel);

            _scope = _scope.Parent!;

            // C 风格 for 在绑定期脱糖为既有的纯循环节点：
            // {
            //     init
            //     while (true)
            //     {
            //         if (condition) { } else break;
            //         body
            //         continue:
            //         update
            //     }
            // }

            _labelCounter++;
            var whileContinueLabel = new BoundLabel($"continue{_labelCounter}");

            var whileBody = ImmutableArray.CreateBuilder<BoundStatement>();

            if (condition != null)
            {
                var emptyThen = new BoundBlockStatement(syntax, ImmutableArray<BoundStatement>.Empty);
                var breakGoto = new BoundGotoStatement(syntax, breakLabel);
                var conditionCheck = new BoundIfStatement(syntax, condition, emptyThen, breakGoto);
                whileBody.Add(conditionCheck);
            }

            whileBody.Add(body);
            whileBody.Add(new BoundLabelStatement(syntax, continueLabel));

            if (update != null)
            {
                whileBody.Add(new BoundExpressionStatement(syntax, update));
            }

            var whileStatement = new BoundWhileStatement(
                syntax,
                new BoundLiteralExpression(syntax, true),
                new BoundBlockStatement(syntax, whileBody.ToImmutable()),
                breakLabel,
                whileContinueLabel);

            var statements = ImmutableArray.CreateBuilder<BoundStatement>();
            if (init != null)
            {
                statements.Add(init);
            }
            statements.Add(whileStatement);

            return new BoundBlockStatement(syntax, statements.ToImmutable());
        }

        private BoundStatement BindLoopBody(StatementSyntax body, out BoundLabel breakLabel, out BoundLabel continueLabel)
        {
            _labelCounter++;
            breakLabel = new BoundLabel($"break{_labelCounter}");
            continueLabel = new BoundLabel($"continue{_labelCounter}");

            _loopStack.Push((breakLabel, continueLabel));
            var boundBody = BindStatement(body);
            _loopStack.Pop();

            return boundBody;
        }

        private BoundStatement BindBreakStatement(BreakStatementSyntax syntax)
        {
            if (_loopStack.Count == 0)
            {
                _diagnostics.ReportInvalidBreakOrContinue(syntax.Keyword.Location, syntax.Keyword.Text);
                return BindErrorStatement(syntax);
            }

            var breakLabel = _loopStack.Peek().BreakLabel;
            return new BoundGotoStatement(syntax, breakLabel);
        }

        private BoundStatement BindContinueStatement(ContinueStatementSyntax syntax)
        {
            if (_loopStack.Count == 0)
            {
                _diagnostics.ReportInvalidBreakOrContinue(syntax.Keyword.Location, syntax.Keyword.Text);
                return BindErrorStatement(syntax);
            }

            // switch 节压入 (switchEnd, null)：continue 需穿透 switch 到最近的循环（C# 语义）
            BoundLabel? continueLabel = null;
            foreach (var entry in _loopStack)
            {
                if (entry.ContinueLabel != null)
                {
                    continueLabel = entry.ContinueLabel;
                    break;
                }
            }

            if (continueLabel == null)
            {
                _diagnostics.ReportError(syntax.Keyword.Location, "continue 只能出现在循环内（不能用于 switch 节）。");
                return BindErrorStatement(syntax);
            }

            return new BoundGotoStatement(syntax, continueLabel);
        }

        private BoundStatement BindReturnStatement(ReturnStatementSyntax syntax)
        {
            var expression = syntax.Expression == null ? null : BindExpression(syntax.Expression);

            if (_function == null)
            {
                if (_isScript)
                {
                    // Ignore because we allow both return with and without values.
                    if (expression == null)
                    {
                        expression = new BoundLiteralExpression(syntax, "");
                    }
                }
                else if (expression != null)
                {
                    // Main does not support return values.
                    _diagnostics.ReportInvalidReturnWithValueInGlobalStatements(syntax.Expression!.Location);
                }
            }
            else
            {
                var isLambdaBody = _lambdaBodyDepth > 0;

                if (_function.ReturnType == TypeSymbol.Void)
                {
                    if (expression != null && !isLambdaBody)
                    {
                        _diagnostics.ReportInvalidReturnExpression(syntax.Expression!.Location, _function.Name);
                    }
                }
                else if (isLambdaBody)
                {
                    // 6e-M22 C5：lambda 体返回类型由推断得出（InferLambdaReturnType），不按外层函数签名转换
                }
                else
                {
                    if (expression == null)
                        _diagnostics.ReportMissingReturnExpression(syntax.Keyword.Location, _function.ReturnType);
                    else
                        expression = BindConversion(syntax.Expression!.Location, expression, _function.ReturnType);
                }
            }

            return new BoundReturnStatement(syntax, expression);
        }

        private BoundStatement BindThrowStatement(ThrowStatementSyntax syntax)
        {
            var expression = BindExpression(syntax.Expression);

            if (!IsExceptionType(expression.Type))
            {
                _diagnostics.ReportThrowTypeNotException(syntax.Expression.Location, expression.Type);
            }

            return new BoundThrowStatement(syntax, expression);
        }

        /// <summary>类型是否为 Exception 或其后代（沿 BaseType 链上溯）。</summary>
        private bool IsExceptionType(TypeSymbol type)
        {
            var exceptionRoot = LookupType("Exception") as ClassTypeSymbol;
            if (exceptionRoot == null)
            {
                return true; // 无 Exception 根（stdlib 缺失）时不额外报错
            }

            for (var current = type as ClassTypeSymbol; current != null; current = current.BaseType)
            {
                if (current == exceptionRoot)
                {
                    return true;
                }
            }

            return false;
        }

        private BoundStatement BindTryStatement(TryStatementSyntax syntax)
        {
            var tryBlock = BindStatement(syntax.TryBlock);

            var catches = ImmutableArray.CreateBuilder<BoundCatchClause>();
            foreach (var catchClause in syntax.Catches)
            {
                var catchType = BindTypeClause(catchClause.Type) ?? TypeSymbol.Error;

                if (catchType != TypeSymbol.Error && !IsExceptionType(catchType))
                {
                    _diagnostics.ReportCatchTypeNotException(catchClause.Type!.Location, catchType);
                }

                _scope = new BoundScope(_scope);
                var variable = BindVariableDeclaration(catchClause.Identifier, isReadOnly: false, catchType);
                var body = BindStatement(catchClause.Body);
                _scope = _scope.Parent!;

                catches.Add(new BoundCatchClause(variable, catchType, body));
            }

            BoundStatement? finallyBlock = null;
            if (syntax.Finally != null)
            {
                finallyBlock = BindStatement(syntax.Finally.Body);
            }

            return new BoundTryStatement(syntax, tryBlock, catches.ToImmutable(), finallyBlock);
        }

        private BoundStatement BindExpressionStatement(ExpressionStatementSyntax syntax)
        {
            // 6e-M22 C5+ 多播事件：订阅（+=/-=）与类内触发（裸名调用）语句级拦截，
            // 脱糖为既有 Bound 节点块（foreach 先例），三后端 + Evaluator 零改动。
            if (syntax.Expression.Kind == SyntaxKind.AssignmentExpression)
            {
                var subscription = TryBindEventSubscription((AssignmentExpressionSyntax)syntax.Expression);
                if (subscription != null)
                {
                    return subscription;
                }
            }

            if (syntax.Expression.Kind == SyntaxKind.CallExpression && _currentClass != null)
            {
                var raiseCall = (CallExpressionSyntax)syntax.Expression;

                if (_currentClass.GetEvent(raiseCall.Identifier.Text) is EventSymbol)
                {
                    return BindEventRaise(syntax, raiseCall.Identifier.Location, raiseCall.Identifier.Text, raiseCall.Arguments);
                }
            }

            var expression = BindExpression(syntax.Expression, canBeVoid: true);

            return new BoundExpressionStatement(syntax, expression);
        }

        private BoundExpression BindExpression(ExpressionSyntax syntax, TypeSymbol targetType)
        {
            return BindConversion(syntax, targetType);
        }

        private BoundExpression BindExpression(ExpressionSyntax syntax, bool canBeVoid = false)
        {
            var result = BindExpressionInternal(syntax);
            if (!canBeVoid && result.Type == TypeSymbol.Void)
            {
                _diagnostics.ReportExpressionMustHaveValue(syntax.Location);
                return new BoundErrorExpression(syntax);
            }

            return result;
        }

        private BoundExpression BindExpressionInternal(ExpressionSyntax syntax)
        {
            switch (syntax.Kind)
            {
                case SyntaxKind.ParenthesizedExpression: return BindParenthesizedExpression((ParenthesizedExpressionSyntax)syntax);
                case SyntaxKind.LiteralExpression: return BindLiteralExpression((LiteralExpressionSyntax)syntax);
                case SyntaxKind.NameExpression: return BindNameExpression((NameExpressionSyntax)syntax);
                case SyntaxKind.AssignmentExpression: return BindAssignmentExpression((AssignmentExpressionSyntax)syntax);
                case SyntaxKind.UnaryExpression: return BindUnaryExpression((UnaryExpressionSyntax)syntax);
                case SyntaxKind.PostfixIncrementExpression: return BindPostfixIncrementExpression((PostfixIncrementExpressionSyntax)syntax);
                case SyntaxKind.BinaryExpression: return BindBinaryExpression((BinaryExpressionSyntax)syntax);
                case SyntaxKind.ConditionalExpression: return BindConditionalExpression((ConditionalExpressionSyntax)syntax);
                case SyntaxKind.CallExpression: return BindCallExpression((CallExpressionSyntax)syntax);
                case SyntaxKind.ArrayCreationExpression: return BindArrayCreationExpression((ArrayCreationExpressionSyntax)syntax);
                case SyntaxKind.ObjectCreationExpression: return BindObjectCreationExpression((ObjectCreationExpressionSyntax)syntax);
                case SyntaxKind.ElementAccessExpression: return BindElementAccessExpression((ElementAccessExpressionSyntax)syntax);
                case SyntaxKind.MemberAccessExpression: return BindMemberAccessExpression((MemberAccessExpressionSyntax)syntax);
                case SyntaxKind.MemberCallExpression: return BindMemberCallExpression((MemberCallExpressionSyntax)syntax);
                case SyntaxKind.CastExpression: return BindCastExpression((CastExpressionSyntax)syntax);
                case SyntaxKind.ThisExpression: return BindThisExpression((ThisExpressionSyntax)syntax);
                case SyntaxKind.BaseExpression: return BindBaseExpression((BaseExpressionSyntax)syntax);
                case SyntaxKind.InterpolatedStringExpression: return BindInterpolatedStringExpression((InterpolatedStringExpressionSyntax)syntax);
                case SyntaxKind.IsExpression: return BindIsExpression((IsExpressionSyntax)syntax);
                case SyntaxKind.AsExpression: return BindAsExpression((AsExpressionSyntax)syntax);
                case SyntaxKind.LambdaExpression: return BindLambdaExpression((LambdaExpressionSyntax)syntax, expectedType: null);
                case SyntaxKind.ByRefArgument: return BindByRefArgument((ByRefArgumentExpressionSyntax)syntax);

                default:
                    throw new Exception($"Unexpected syntax {syntax.Kind}");
            }
        }

        /// <summary>byref 实参绑定（6e-M23 R3）：实参须为可赋值 lvalue——变量/实例或静态字段（非只读）/数组元素。</summary>
        private BoundExpression BindByRefArgument(ByRefArgumentExpressionSyntax syntax)
        {
            var inner = BindExpression(syntax.Expression);

            var isLValue = inner switch
            {
                BoundVariableExpression variable => !variable.Variable.IsReadOnly,
                // 数组元素可作 byref 目标；string 索引为只读字符（对齐 C#）
                BoundElementAccessExpression element => element.Target.Type != TypeSymbol.String,
                BoundMemberAccessExpression member => member.Field != null && !member.Field.IsReadOnly,
                _ => false,
            };

            if (!isLValue)
            {
                _diagnostics.ReportByRefArgumentNotLValue(syntax.Expression.Location, syntax.IsRef ? "ref" : "out");
                return new BoundErrorExpression(syntax);
            }

            return new BoundByRefArgument(syntax, inner, syntax.IsRef);
        }

        /// <summary>实参转换统一入口（6e-M23 R3）：byref 形参做修饰符对应与精确类型校验；值形参拒绝带修饰符实参；其余走 BindConversion。</summary>
        private BoundExpression BindArgumentConversion(TextLocation location, BoundExpression argument, ParameterSymbol parameter)
        {
            if (parameter.IsByRef)
            {
                return CheckByRefArgument(location, argument, parameter);
            }

            if (argument is BoundByRefArgument stray)
            {
                _diagnostics.ReportByRefModifierOnValueParameter(location, stray.IsRef ? "ref" : "out");
                return new BoundErrorExpression(stray.Syntax);
            }

            return BindConversion(location, argument, parameter.Type);
        }

        /// <summary>byref 形参-实参对应校验（6e-M23 R3）：修饰符一致 + 类型精确相等（对齐 C#，byref 不参与隐式转换）。</summary>
        private BoundExpression CheckByRefArgument(TextLocation location, BoundExpression argument, ParameterSymbol parameter)
        {
            var expectedModifier = parameter.IsRef ? "ref" : "out";

            if (argument is not BoundByRefArgument wrapped)
            {
                _diagnostics.ReportMissingByRefModifier(location, expectedModifier);
                return new BoundErrorExpression(argument.Syntax);
            }

            if (wrapped.IsRef != parameter.IsRef)
            {
                _diagnostics.ReportByRefModifierMismatch(location, expectedModifier);
                return new BoundErrorExpression(wrapped.Syntax);
            }

            if (wrapped.Type != TypeSymbol.Error && wrapped.Expression.Type != parameter.Type)
            {
                _diagnostics.ReportCannotConvert(location, wrapped.Expression.Type, parameter.Type);
                return new BoundErrorExpression(wrapped.Syntax);
            }

            return wrapped;
        }

        /// <summary>插值字符串 → 字符串 <c>+</c> 链（每洞转 string；含对齐/格式时包 BoundFormatExpression）；常量折叠天然不启用（转换/格式节点无 ConstantValue）。</summary>
        private BoundExpression BindInterpolatedStringExpression(InterpolatedStringExpressionSyntax syntax)
        {
            BoundExpression? result = null;

            foreach (var content in syntax.Contents)
            {
                if (content is InterpolatedStringTextSyntax text)
                {
                    var value = (string)text.TextToken.Value!;
                    result = AppendInterpolation(result, new BoundLiteralExpression(text, value), text);
                }
                else if (content is InterpolationSyntax interpolation)
                {
                    var bound = BindExpression(interpolation.Expression);

                    BoundExpression formatted;
                    if (interpolation.Alignment != null || interpolation.FormatToken != null)
                    {
                        int? width = null;
                        if (interpolation.Alignment != null)
                        {
                            var boundAlignment = BindExpression(interpolation.Alignment);
                            if (!TryGetIntConstant(boundAlignment, out var intValue))
                            {
                                _diagnostics.ReportError(interpolation.Alignment.Location, "插值洞的对齐宽度必须为整数常量。");
                                formatted = new BoundErrorExpression(interpolation.Alignment);
                            }
                            else
                            {
                                width = intValue;
                                formatted = new BoundFormatExpression(interpolation, bound, width, FormatOf(interpolation));
                            }
                        }
                        else
                        {
                            formatted = new BoundFormatExpression(interpolation, bound, null, FormatOf(interpolation));
                        }
                    }
                    else
                    {
                        formatted = BindConversion(interpolation.Expression.Location, bound, TypeSymbol.String, allowExplicit: true);
                    }

                    result = AppendInterpolation(result, formatted, interpolation);
                }
            }

            return result ?? new BoundLiteralExpression(syntax, "");
        }

        private static string? FormatOf(InterpolationSyntax interpolation)
        {
            return interpolation.FormatToken == null ? null : (string)interpolation.FormatToken.Value!;
        }

        private static BoundExpression AppendInterpolation(BoundExpression? left, BoundExpression right, SyntaxNode syntax)
        {
            if (left == null)
            {
                return right;
            }

            var op = BoundBinaryOperator.Bind(SyntaxKind.PlusToken, TypeSymbol.String, TypeSymbol.String)!;
            return new BoundBinaryExpression(syntax, left, op, right);
        }

        private BoundExpression BindParenthesizedExpression(ParenthesizedExpressionSyntax syntax)
        {
            return BindExpression(syntax.Expression);
        }

        private BoundExpression BindLiteralExpression(LiteralExpressionSyntax syntax)
        {
            // 6e-M19 M5-a：null 字面量 → Null 类型（绑定期经 BindConversion 落到目标引用型）
            if (syntax.LiteralToken.Kind == SyntaxKind.NullKeyword)
            {
                return new BoundLiteralExpression(syntax, null!, TypeSymbol.Null);
            }

            var value = syntax.Value ?? 0;

            return new BoundLiteralExpression(syntax, value);
        }

        private BoundExpression BindNameExpression(NameExpressionSyntax syntax)
        {
            var name = syntax.IdentifierToken.Text;
            if (syntax.IdentifierToken.IsMissing)
            {
                // This means the token was inserted by the parser, We already
                // reported error so we can just return an error expression.
                return new BoundErrorExpression(syntax);
            }

            var lookup = _scope.TryLookupSymbol(name);

            if (lookup is VariableSymbol variable)
            {
                return new BoundVariableExpression(syntax, variable);
            }

            // 类方法内：裸标识符可引用本类字段（this 字段）
            if (_currentClass != null)
            {
                var field = _currentClass.GetField(name);
                if (field != null)
                {
                    if (_function?.IsStatic == true && !field.IsStatic)
                    {
                        _diagnostics.ReportError(syntax.IdentifierToken.Location, $"静态方法中不能访问实例字段 '{name}'。");
                        return new BoundErrorExpression(syntax);
                    }

                    var thisExpression = new BoundThisExpression(syntax, _currentClass);
                    return new BoundMemberAccessExpression(syntax, field.Type, thisExpression, name, field);
                }

                // 裸标识符可引用本类/基类属性（getter）
                var property = _currentClass.GetProperty(name);
                if (property != null && property.Getter != null)
                {
                    if (_function?.IsStatic == true && !property.Getter.IsStatic)
                    {
                        _diagnostics.ReportError(syntax.IdentifierToken.Location, $"静态方法中不能访问实例属性 '{name}'。");
                        return new BoundErrorExpression(syntax);
                    }

                    if (!IsAccessibleMember(property.Getter.Visibility, property.Getter.ContainingClass!))
                    {
                        _diagnostics.ReportCannotAccessMember(syntax.IdentifierToken.Location, name, property.Getter.Visibility);
                        return new BoundErrorExpression(syntax);
                    }

                    var thisExpression = new BoundThisExpression(syntax, _currentClass);
                    return new BoundMemberCallExpression(syntax, thisExpression, property.Getter.Name, ImmutableArray<BoundExpression>.Empty, property.Type, property.Getter);
                }
            }

            // 函数值（6e-M22 C4）：类方法内裸标识符可引用本类方法（方法组 → 一等函数值）
            if (_currentClass != null)
            {
                var groupCandidates = _currentClass.GetMethods(name).Where(m => !m.IsConstructor).ToImmutableArray();
                if (groupCandidates.Length == 1)
                {
                    return CreateFunctionValue(syntax, receiver: null, groupCandidates[0]);
                }
            }

            // 函数值（6e-M22 C4）：裸名引用顶层/命名空间函数（恰一候选，重载歧义报诊断）
            var functionCandidates = _scope.TryLookupFunctions(name);
            if (functionCandidates != null)
            {
                var functions = functionCandidates.Value.Where(f => !f.IsConstructor).ToImmutableArray();
                if (functions.Length == 1)
                {
                    return CreateFunctionValue(syntax, receiver: null, functions[0]);
                }

                if (functions.Length > 1)
                {
                    _diagnostics.ReportError(syntax.IdentifierToken.Location, $"函数 '{name}' 存在多个重载，函数值引用须无歧义。");
                    return new BoundErrorExpression(syntax);
                }
            }

            if (lookup == null)
            {
                _diagnostics.ReportUndefinedVariable(syntax.IdentifierToken.Location, name);
            }
            else
            {
                _diagnostics.ReportNotAVariable(syntax.IdentifierToken.Location, name);
            }

            return new BoundErrorExpression(syntax);
        }

        /// <summary>方法组 → 函数值（6e-M22 C4）：类型 = 签名形状（接收者作环境槽，不入参数表）；byref 签名不可转函数类型（6e-M23 R3）。</summary>
        private BoundExpression CreateFunctionValue(SyntaxNode syntax, BoundExpression? receiver, FunctionSymbol function)
        {
            if (function.Parameters.Any(p => p.IsByRef))
            {
                _diagnostics.ReportFunctionTypeByRefParameter(syntax.Location);
                return new BoundErrorExpression(syntax);
            }

            var parameterTypes = function.Parameters.Select(p => p.Type).ToImmutableArray();
            var functionType = FunctionTypeSymbol.Get(parameterTypes, function.ReturnType);

            return new BoundFunctionValueExpression(syntax, function, receiver, body: null, functionType);
        }

        /// <summary>
        /// Lambda 提升（6e-M22 C4）：`(x: int) =&gt; expr|block` → 合成静态方法符号 + 已绑定体随函数值携带
        /// （BindProgram 后处理入 Functions 清单）。显式参数类型直接落签名；隐式参数须有目标函数类型
        /// （期望类型经 <see cref="BindConversion"/> 钩子下推）；无目标且含隐式参数报诊断。
        /// </summary>
        private BoundExpression BindLambdaExpression(LambdaExpressionSyntax syntax, FunctionTypeSymbol? expectedType)
        {
            if (!syntax.HasExplicitParameterTypes && expectedType == null)
            {
                _diagnostics.ReportError(syntax.Location, "隐式类型 lambda 参数需要目标函数类型（如赋值/传参位置），当前上下文无法推导；请显式标注参数类型。");
                return new BoundErrorExpression(syntax);
            }

            if (expectedType != null && syntax.Parameters.Count != expectedType.ParameterTypes.Length)
            {
                _diagnostics.ReportError(syntax.Location, $"lambda 参数数 {syntax.Parameters.Count} 与目标函数类型的 {expectedType.ParameterTypes.Length} 不符。");
                return new BoundErrorExpression(syntax);
            }

            var parameterSymbols = ImmutableArray.CreateBuilder<ParameterSymbol>();
            var parameterTypes = ImmutableArray.CreateBuilder<TypeSymbol>();
            for (var i = 0; i < syntax.Parameters.Count; i++)
            {
                var parameterSyntax = syntax.Parameters[i];
                var parameterType = parameterSyntax.Type.Identifier.IsMissing
                    ? expectedType!.ParameterTypes[i]
                    : BindTypeClause(parameterSyntax.Type);

                if (parameterType == null)
                {
                    return new BoundErrorExpression(syntax);
                }

                parameterTypes.Add(parameterType);
                parameterSymbols.Add(new ParameterSymbol(parameterSyntax.Identifier.Text, parameterType, i));
            }

            // 体绑定：子作用域声明参数（沿用当前 Binder 上下文——类/静态/别名等语义一致）
            var lambdaOuterScope = _scope;
            _scope = new BoundScope(lambdaOuterScope);
            foreach (var parameter in parameterSymbols)
            {
                _scope.TryDeclareVariable(parameter);
            }

            _lambdaBodyDepth++;

            BoundBlockStatement body;
            TypeSymbol returnType;

            try
            {
                if (syntax.Body is BlockStatementSyntax blockSyntax)
                {
                    body = (BoundBlockStatement)BindStatement(blockSyntax);
                    returnType = InferLambdaReturnType(body, syntax);
                }
                else
                {
                    var expression = BindExpression((ExpressionSyntax)syntax.Body);
                    if (expression.Type == TypeSymbol.Error)
                    {
                        return new BoundErrorExpression(syntax);
                    }

                    returnType = expression.Type;
                    if (returnType != TypeSymbol.Void)
                    {
                        var returnStatement = new BoundReturnStatement(syntax.Body, expression);
                        body = new BoundBlockStatement(syntax.Body, ImmutableArray.Create<BoundStatement>(returnStatement));
                    }
                    else
                    {
                        var expressionStatement = new BoundExpressionStatement(syntax.Body, expression);
                        body = new BoundBlockStatement(syntax.Body, ImmutableArray.Create<BoundStatement>(expressionStatement));
                    }
                }
            }
            finally
            {
                _lambdaBodyDepth--;
                _scope = lambdaOuterScope;
            }

            if (expectedType != null && returnType != expectedType.ReturnType &&
                Conversion.Classify(returnType, expectedType.ReturnType) is { Exists: true } conversion && !conversion.IsIdentity)
            {
                // 返回类型可（显式）转换到目标时自动补转换节点
                body = ConvertLambdaBodyReturns(body, expectedType.ReturnType, syntax);
                returnType = expectedType.ReturnType;
            }
            else if (returnType != expectedType?.ReturnType && expectedType != null)
            {
                _diagnostics.ReportCannotConvert(syntax.Location, returnType, expectedType.ReturnType);
                return new BoundErrorExpression(syntax);
            }

            // 6e-M22：void lambda 体尾部补隐式 return（与 BuildFunctionBody 的 Lowerer 行为对齐）
            if (returnType == TypeSymbol.Void)
            {
                var voidReturn = new BoundReturnStatement(syntax.Body, null);
                body = new BoundBlockStatement(body.Syntax, body.Statements.Add(voidReturn));
            }

            var sequence = System.Threading.Interlocked.Increment(ref _lambdaGlobalSequence);
            var functionName = $"__Lambda${sequence}";

            // 捕获分析（6e-M22 C5）：遍历体引用 → 外层局部/参数 = 捕获集；
            // 体内部声明的变量不属于捕获。this 捕获留 C5 后续。
            var ownSymbols = new HashSet<VariableSymbol>(parameterSymbols);
            var referencedVariables = new HashSet<VariableSymbol>();
            var declaredInBody = new HashSet<VariableSymbol>();
            CollectVariableUsage(body, referencedVariables, declaredInBody);

            var captures = new List<VariableSymbol>();
            foreach (var variable in referencedVariables)
            {
                if (ownSymbols.Contains(variable) || declaredInBody.Contains(variable))
                {
                    continue;
                }

                if (variable is GlobalVariableSymbol)
                {
                    continue; // 全局变量静态存储，无需环境承载
                }

                if (variable is ParameterSymbol { IsByRef: true } byRefParameter)
                {
                    _diagnostics.ReportCaptureOfByRefParameter(syntax.Location, byRefParameter.Name);
                    return new BoundErrorExpression(syntax);
                }

                captures.Add(variable);
            }

            FunctionSymbol? environmentOwner = null;
            ClassTypeSymbol? environmentClass = null;

            if (captures.Count > 0)
            {
                environmentOwner = _environmentOwner ?? _function;

                if (environmentOwner == null || environmentOwner.Syntax is LambdaExpressionSyntax)
                {
                    _diagnostics.ReportError(syntax.Location, "lambda 捕获需要宿主函数上下文（顶层脚本暂不支持）。");
                    return new BoundErrorExpression(syntax);
                }

                if (!_environmentClasses.TryGetValue(environmentOwner, out environmentClass))
                {
                    environmentClass = new ClassTypeSymbol($"__Env_{environmentOwner.Name}", string.Empty, Visibility.Private, declaration: null)
                    {
                        BaseType = ClassTypeSymbol.SystemObject,
                    };
                    _environmentClasses[environmentOwner] = environmentClass;
                }

                foreach (var captured in captures)
                {
                    captured.IsCaptured = true;
                    environmentClass.AddField(new FieldSymbol(captured.Name, captured.Type, Visibility.Public, environmentClass));
                }

                environmentOwner.CapturedVariables ??= new List<VariableSymbol>();
                foreach (var captured in captures)
                {
                    if (!environmentOwner.CapturedVariables.Contains(captured))
                    {
                        environmentOwner.CapturedVariables.Add(captured);
                    }
                }

                environmentOwner.EnvironmentClass = environmentClass;
            }

            var function = new FunctionSymbol(functionName, parameterSymbols.ToImmutable(), returnType, declaration: null, syntax: syntax, containingClass: _currentClass, visibility: Visibility.Private)
            {
                IsStatic = true,
                EnvironmentOwner = environmentOwner,
                IsLambdaWithEnvironment = environmentClass != null,
                EnvironmentClass = environmentClass,
            };

            var computedType = FunctionTypeSymbol.Get(parameterTypes.ToImmutable(), returnType);
            return new BoundFunctionValueExpression(syntax, function, receiver: null, body, computedType, environmentClass);
        }

        /// <summary>遍历绑定树收集变量引用与体内声明（6e-M22 C5 捕获分析用）。</summary>
        private static void CollectVariableUsage(BoundNode node, HashSet<VariableSymbol> references, HashSet<VariableSymbol> declarations)
        {
            switch (node)
            {
                case BoundVariableExpression variableExpression:
                    references.Add(variableExpression.Variable);
                    break;
                case BoundAssignmentExpression assignment:
                    references.Add(assignment.Variable);
                    break;
                case BoundCompoundAssignmentExpression compoundAssignment:
                    references.Add(compoundAssignment.Variable);
                    break;
                case BoundVariableDeclaration declaration:
                    declarations.Add(declaration.Variable);
                    references.Add(declaration.Variable); // 初始化器读旧值场景
                    break;
            }

            foreach (var child in Compilation.BoundChildren(node))
            {
                CollectVariableUsage(child, references, declarations);
            }
        }

        /// <summary>块体返回类型推断：取首条带值 return 的类型；无则 void。</summary>
        private static TypeSymbol InferLambdaReturnType(BoundBlockStatement body, LambdaExpressionSyntax syntax)
        {
            foreach (var statement in body.Statements)
            {
                if (statement is BoundReturnStatement { Expression: { } expression } &&
                    expression.Type != TypeSymbol.Void)
                {
                    return expression.Type;
                }
            }

            return TypeSymbol.Void;
        }

        /// <summary>块体内全部带值 return 补转换到目标返回类型（表达式体已在合成前处理）。</summary>
        private BoundBlockStatement ConvertLambdaBodyReturns(BoundBlockStatement body, TypeSymbol targetType, SyntaxNode syntax)
        {
            var statements = ImmutableArray.CreateBuilder<BoundStatement>();
            foreach (var statement in body.Statements)
            {
                if (statement is BoundReturnStatement { Expression: { } expression } returnStatement)
                {
                    statements.Add(new BoundReturnStatement(returnStatement.Syntax, BindConversion(returnStatement.Syntax.Location, expression, targetType)));
                    continue;
                }

                statements.Add(statement);
            }

            return new BoundBlockStatement(body.Syntax, statements.ToImmutable());
        }

        private BoundExpression BindAssignmentExpression(AssignmentExpressionSyntax syntax)
        {
            var boundTarget = BindExpression(syntax.Target);
            var boundExpression = BindExpression(syntax.Expression);

            // 属性写：obj.Name = v → set_Name(v)
            if (boundTarget is BoundMemberCallExpression propertyGetCall &&
                propertyGetCall.Method != null &&
                propertyGetCall.Method.Name.StartsWith("get_"))
            {
                var propertyName = propertyGetCall.Method.Name.Substring(4);
                var property = propertyGetCall.Method.ContainingClass!.GetProperty(propertyName);
                if (property?.Setter != null && syntax.AssignmentToken.Kind == SyntaxKind.EqualsToken)
                {
                    if (!IsAccessibleMember(property.Setter.Visibility, property.Setter.ContainingClass!))
                    {
                        _diagnostics.ReportCannotAccessMember(syntax.AssignmentToken.Location, propertyName, property.Setter.Visibility);
                        return new BoundErrorExpression(syntax);
                    }

                    var converted = BindConversion(syntax.Expression.Location, boundExpression, property.Type);
                    // 保留 getter 调用的实参（普通属性为空；索引器为 [下标]，须随 setter 透传），
                    // 否则 list[i] = v 会因丢失下标实参导致 set_Item 调用栈不平衡（InvalidProgramException）。
                    return new BoundMemberCallExpression(syntax, propertyGetCall.Expression, property.Setter.Name, propertyGetCall.Arguments.Add(converted), TypeSymbol.Void, property.Setter, propertyGetCall.IsBase);
                }

                _diagnostics.ReportCannotAssign(syntax.AssignmentToken.Location, propertyName);
                return new BoundErrorExpression(syntax);
            }

            if (boundTarget is BoundVariableExpression variableTarget)
            {
                var variable = variableTarget.Variable;

                if (variable.IsReadOnly)
                {
                    _diagnostics.ReportCannotAssign(syntax.AssignmentToken.Location, variable.Name);
                }

                if (syntax.AssignmentToken.Kind != SyntaxKind.EqualsToken)
                {
                    var equivalentOperatorTokenKind = SyntaxFacts.GetBinaryOperatorOfAssignmentOperator(syntax.AssignmentToken.Kind);
                    var boundOperator = BoundBinaryOperator.Bind(equivalentOperatorTokenKind, variable.Type, boundExpression.Type);

                    // 6e-M21 Phase 7：数值复合赋值走二元提升（x: i64 += 50 等），失败再报未定义
                    if (boundOperator == null && IsNumeric(variable.Type) && IsNumeric(boundExpression.Type))
                    {
                        var commonType = GetBinaryNumericResultType(variable.Type, boundExpression.Type, equivalentOperatorTokenKind);
                        if (commonType != null)
                        {
                            boundExpression = BindConversion(boundExpression.Syntax.Location, boundExpression, commonType, allowExplicit: false);
                            boundOperator = BoundBinaryOperator.Bind(equivalentOperatorTokenKind, commonType, commonType);
                        }
                    }

                    if (boundOperator == null)
                    {
                        _diagnostics.ReportUndefinedBinaryOperator(syntax.AssignmentToken.Location, syntax.AssignmentToken.Text, variable.Type, boundExpression.Type);
                        return new BoundErrorExpression(syntax);
                    }

                    var convertedExpression = BindConversion(syntax.Expression.Location, boundExpression, variable.Type);

                    return new BoundCompoundAssignmentExpression(syntax, variable, boundOperator, convertedExpression);
                }
                else
                {
                    var convertedExpression = BindConversion(syntax.Expression.Location, boundExpression, variable.Type);

                    return new BoundAssignmentExpression(syntax, variable, convertedExpression);
                }
            }

            if (boundTarget is BoundElementAccessExpression elementTarget && elementTarget.Target.Type == TypeSymbol.String)
            {
                _diagnostics.ReportStringIndexNotAssignable(syntax.AssignmentToken.Location);
                return boundExpression;
            }

            if (boundTarget is BoundElementAccessExpression arrayElementTarget && syntax.AssignmentToken.Kind == SyntaxKind.EqualsToken)
            {
                var convertedExpression = BindConversion(syntax.Expression.Location, boundExpression, arrayElementTarget.Type);

                return new BoundElementAssignmentExpression(syntax, arrayElementTarget.Type, arrayElementTarget, convertedExpression);
            }

            // 索引器赋值：list[i] = x → set_Item（facade 经普通调用 → IL 直连 BCL；其余走 Cocoa 体）
            if (boundTarget is BoundMemberCallExpression mcIndexer && mcIndexer.Method?.ContainingProperty?.IsIndexer == true && syntax.AssignmentToken.Kind == SyntaxKind.EqualsToken)
            {
                var indexer = mcIndexer.Method.ContainingProperty!;
                if (indexer.Setter != null)
                {
                    var converted = BindConversion(syntax.Expression.Location, boundExpression, indexer.Type);
                    return new BoundMemberCallExpression(syntax, mcIndexer.Expression, "set_Item", mcIndexer.Arguments.Add(converted), TypeSymbol.Void, indexer.Setter);
                }

                _diagnostics.ReportCannotAssign(syntax.AssignmentToken.Location, "Item");
                return boundExpression;
            }

            if (boundTarget is BoundCallExpression bcIndexer && bcIndexer.Function.ContainingProperty?.IsIndexer == true && syntax.AssignmentToken.Kind == SyntaxKind.EqualsToken)
            {
                var indexer = bcIndexer.Function.ContainingProperty!;
                if (indexer.Setter != null)
                {
                    var converted = BindConversion(syntax.Expression.Location, boundExpression, indexer.Type);
                    return new BoundCallExpression(syntax, indexer.Setter, bcIndexer.Arguments.Add(converted));
                }

                _diagnostics.ReportCannotAssign(syntax.AssignmentToken.Location, "Item");
                return boundExpression;
            }

            if (boundTarget is BoundMemberAccessExpression memberTarget && memberTarget.Field != null &&
                (syntax.AssignmentToken.Kind == SyntaxKind.EqualsToken ||
                 syntax.AssignmentToken.Kind == SyntaxKind.PlusEqualsToken ||
                 syntax.AssignmentToken.Kind == SyntaxKind.MinusEqualsToken))
            {
                // 6e-M22 C5+ 多播：事件后备字段的 += / -= 已在语句级拦截（TryBindEventSubscription）；
                // 事件不能直接赋值（含 `=`），只能经订阅语法或类内触发。
                if (IsEventBackingField(memberTarget.Field))
                {
                    _diagnostics.ReportEventNotAValue(syntax.AssignmentToken.Location, memberTarget.Field.Name);
                    return new BoundErrorExpression(syntax);
                }

                if (!IsAccessibleMember(memberTarget.Field.Visibility, memberTarget.Field.ContainingClass))
                {
                    _diagnostics.ReportCannotAccessMember(syntax.AssignmentToken.Location, memberTarget.Field.Name, memberTarget.Field.Visibility);
                    return new BoundErrorExpression(syntax);
                }

                if (memberTarget.Field.IsReadonly && _function?.IsConstructor != true)
                {
                    _diagnostics.ReportCannotAssign(syntax.AssignmentToken.Location, memberTarget.Field.Name);
                    return new BoundErrorExpression(syntax);
                }

                var convertedExpression = BindConversion(syntax.Expression.Location, boundExpression, memberTarget.Type);

                return new BoundMemberAssignmentExpression(syntax, memberTarget.Target, memberTarget.Field, convertedExpression);
            }

            if (boundTarget.Type != TypeSymbol.Error)
            {
                _diagnostics.ReportCannotAssign(syntax.AssignmentToken.Location, boundTarget.Type.Name);
            }

            return boundExpression;
        }

        private BoundExpression BindPostfixIncrementExpression(PostfixIncrementExpressionSyntax syntax)
        {
            return BindIncrementOrDecrement(syntax, syntax.Operand, syntax.OperatorToken);
        }

        private BoundExpression BindIncrementOrDecrement(SyntaxNode syntax, ExpressionSyntax operandSyntax, SyntaxToken operatorToken)
        {
            var boundTarget = BindExpression(operandSyntax);

            if (boundTarget is BoundVariableExpression variableTarget)
            {
                var variable = variableTarget.Variable;

                if (variable.IsReadOnly)
                {
                    _diagnostics.ReportCannotAssign(operatorToken.Location, variable.Name);
                }

                // x++/++x → x = x + 1；x--/--x → x = x - 1
                var operatorTokenKind = operatorToken.Kind == SyntaxKind.PlusPlusToken
                    ? SyntaxKind.PlusToken
                    : SyntaxKind.MinusToken;
                var boundOperator = BoundBinaryOperator.Bind(operatorTokenKind, variable.Type, variable.Type);

                if (boundOperator == null)
                {
                    _diagnostics.ReportUndefinedBinaryOperator(operatorToken.Location, operatorToken.Text, variable.Type, variable.Type);
                    return new BoundErrorExpression(syntax);
                }

                var one = new BoundLiteralExpression(syntax, 1);
                var convertedOne = BindConversion(operatorToken.Location, one, variable.Type);
                var binary = new BoundBinaryExpression(syntax, variableTarget, boundOperator, convertedOne);

                return new BoundAssignmentExpression(syntax, variable, binary);
            }

            if (boundTarget.Type != TypeSymbol.Error)
            {
                _diagnostics.ReportCannotAssign(operatorToken.Location, boundTarget.Type.Name);
            }

            return new BoundErrorExpression(syntax);
        }

        private BoundExpression BindArrayCreationExpression(ArrayCreationExpressionSyntax syntax)
        {
            var elementType = LookupType(syntax.Identifier.Text);
            if (elementType == null)
            {
                _diagnostics.ReportUndefinedType(syntax.Identifier.Location, syntax.Identifier.Text);
                return new BoundErrorExpression(syntax);
            }

            var arrayType = TypeSymbol.ArrayOf(elementType);
            BoundExpression length;
            var initializers = ImmutableArray.CreateBuilder<BoundExpression>();

            if (syntax.Size != null)
            {
                length = BindExpression(syntax.Size);
                if (length.Type != TypeSymbol.Error && length.Type != TypeSymbol.Int32)
                {
                    _diagnostics.ReportCannotConvert(syntax.Size.Location, length.Type, TypeSymbol.Int32);
                    length = new BoundErrorExpression(syntax.Size);
                }
            }
            else
            {
                length = new BoundLiteralExpression(syntax, syntax.Elements.Count);
            }

            foreach (var elementSyntax in syntax.Elements)
            {
                var element = BindConversion(elementSyntax.Location, BindExpression(elementSyntax), elementType);
                initializers.Add(element);
            }

            return new BoundArrayCreationExpression(syntax, arrayType, length, initializers.ToImmutable());
        }

        private BoundExpression BindObjectCreationExpression(ObjectCreationExpressionSyntax syntax)
        {
            // 泛型对象创建（6e-M20）：`new Box<int>(…)` → 实例化类
            ClassTypeSymbol? classType;
            if (syntax.TypeArguments != null)
            {
                classType = BindGenericTypeName(syntax.Identifier, syntax.TypeArguments.Arguments) as ClassTypeSymbol;

                if (classType == null)
                {
                    return new BoundErrorExpression(syntax);
                }
            }
            else
            {
                classType = LookupType(syntax.Identifier.Text) as ClassTypeSymbol;
            }

            if (classType == null)
            {
                _diagnostics.ReportUndefinedType(syntax.Identifier.Location, syntax.Identifier.Text);
                return new BoundErrorExpression(syntax);
            }

            if (classType.IsStatic)
            {
                _diagnostics.ReportError(syntax.Identifier.Location, $"不能实例化静态类 '{classType.Name}'。");
                return new BoundErrorExpression(syntax);
            }

            if (classType.IsInterface)
            {
                _diagnostics.ReportError(syntax.Identifier.Location, $"不能实例化接口 '{classType.Name}'。");
                return new BoundErrorExpression(syntax);
            }

            if (classType.IsAbstract)
            {
                _diagnostics.ReportError(syntax.Identifier.Location, $"不能实例化抽象类 '{classType.Name}'。");
                return new BoundErrorExpression(syntax);
            }

            var arguments = ImmutableArray.CreateBuilder<BoundExpression>();

            foreach (var argumentSyntax in syntax.Arguments)
            {
                arguments.Add(BindExpression(argumentSyntax));
            }

            // 参数个数校验：构造函数签名 == 实参个数
            var ctor = classType.GetMethod(classType.Name);
            if (ctor != null)
            {
                if (!IsAccessibleMember(ctor.Visibility, ctor.ContainingClass!))
                {
                    _diagnostics.ReportCannotAccessMember(syntax.Identifier.Location, classType.Name, ctor.Visibility);
                    return new BoundErrorExpression(syntax);
                }

                if (ctor.Parameters.Length != arguments.Count)
                {
                    _diagnostics.ReportWrongArgumentCount(syntax.Identifier.Location, classType.Name, ctor.Parameters.Length, arguments.Count);
                    return new BoundErrorExpression(syntax);
                }
            }

            for (var i = 0; i < arguments.Count; i++)
            {
                if (ctor != null)
                {
                    arguments[i] = BindConversion(arguments[i].Syntax.Location, arguments[i], ctor.Parameters[i].Type);
                }
            }

            return new BoundObjectCreationExpression(syntax, classType, arguments.ToImmutable());
        }

        private BoundExpression BindElementAccessExpression(ElementAccessExpressionSyntax syntax)
        {
            var boundTarget = BindExpression(syntax.Expression);
            var boundIndex = BindExpression(syntax.Index);

            if (boundTarget.Type == TypeSymbol.Error)
            {
                return new BoundErrorExpression(syntax);
            }

            if (boundTarget.Type == TypeSymbol.String)
            {
                // string[index] → char（下标须为 i32）
                if (boundIndex.Type != TypeSymbol.Error && boundIndex.Type != TypeSymbol.Int32)
                {
                    _diagnostics.ReportCannotConvert(syntax.Index.Location, boundIndex.Type, TypeSymbol.Int32);
                    boundIndex = new BoundErrorExpression(syntax.Index);
                }

                return new BoundElementAccessExpression(syntax, TypeSymbol.Char, boundTarget, boundIndex);
            }

            // 索引器（this[...]）：重定向到 get_Item（facade 经普通调用 → IL 直连 BCL；其余走 Cocoa 体）
            if (boundTarget.Type is ClassTypeSymbol cls)
            {
                var indexer = cls.GetIndexer();
                if (indexer != null && indexer.Getter != null)
                {
                    // 下标须可转换为索引器参数类型（List 为 i32；Dictionary 为 K；不可硬编码 i32）
                    var indexParameterType = indexer.Getter.Parameters[0].Type;
                    if (boundIndex.Type != TypeSymbol.Error)
                    {
                        boundIndex = BindConversion(syntax.Index.Location, boundIndex, indexParameterType);
                    }

                    var facade = cls.IsFacadeClass || (cls is InstantiatedTypeSymbol inst && inst.GenericDefinition?.IsFacadeClass == true);
                    if (facade)
                    {
                        return new BoundCallExpression(syntax, indexer.Getter, ImmutableArray.Create(boundTarget, boundIndex));
                    }

                    return new BoundMemberCallExpression(syntax, boundTarget, "get_Item", ImmutableArray.Create(boundIndex), indexer.Type, indexer.Getter);
                }
            }

            if (boundTarget.Type.ElementType == null)
            {
                _diagnostics.ReportIndexRequiresArray(syntax.Location, boundTarget.Type);
                return new BoundErrorExpression(syntax);
            }

            return new BoundElementAccessExpression(syntax, boundTarget.Type.ElementType, boundTarget, boundIndex);
        }

        private BoundExpression BindMemberAccessExpression(MemberAccessExpressionSyntax syntax)
        {
            var identifier = syntax.IdentifierToken.Text;

            // 静态字段访问：MathHelpers.Count / My.App.Utils.Count
            if (ResolveDottedTypeName(syntax.Expression) is string staticTypeName &&
                LookupType(staticTypeName) is ClassTypeSymbol staticType &&
                staticType.GetField(identifier) is FieldSymbol staticField &&
                staticField.IsStatic)
            {
                if (!IsAccessibleMember(staticField.Visibility, staticField.ContainingClass))
                {
                    _diagnostics.ReportCannotAccessMember(syntax.IdentifierToken.Location, identifier, staticField.Visibility);
                    return new BoundErrorExpression(syntax);
                }

                return new BoundMemberAccessExpression(syntax, staticField.Type, new BoundStaticTypeExpression(syntax.Expression, staticType), identifier, staticField);
            }

            // 枚举成员访问（Color.Red / My.App.Color.Red）：左侧为枚举类型名 → 折叠为常量字面量
            if (ResolveDottedTypeName(syntax.Expression) is string enumTypeName &&
                LookupType(enumTypeName) is EnumTypeSymbol enumType)
            {
                if (enumType.TryGetMember(syntax.IdentifierToken.Text, out var value))
                {
                    return new BoundLiteralExpression(syntax, value, enumType);
                }

                _diagnostics.ReportEnumMemberNotDefined(syntax.IdentifierToken.Location, enumType.Name, syntax.IdentifierToken.Text);
                return new BoundErrorExpression(syntax);
            }

            // 6e-M19 M2-b：facade 静态常量（i32.MaxValue / Double.NaN 等）——左侧为基元类型名，折叠为字面量
            if (ResolveDottedTypeName(syntax.Expression) is string facadeConstTypeName &&
                LookupType(facadeConstTypeName) is TypeSymbol constReceiverType &&
                FacadeNameOfType(constReceiverType) is string constFacadeName &&
                FacadeConstants.TryGetValue(constFacadeName, out var constTable) &&
                constTable.TryGetValue(identifier, out var constantValue))
            {
                return new BoundLiteralExpression(syntax, constantValue);
            }

            var boundTarget = BindExpression(syntax.Expression);

            if (boundTarget.Type == TypeSymbol.Error)
            {
                return new BoundErrorExpression(syntax);
            }

            // 类字段访问：point._x
            if (boundTarget.Type is ClassTypeSymbol classType)
            {
                var field = classType.GetField(identifier);
                if (field != null)
                {
                    if (!IsAccessibleMember(field.Visibility, field.ContainingClass))
                    {
                        _diagnostics.ReportCannotAccessMember(syntax.IdentifierToken.Location, identifier, field.Visibility);
                        return new BoundErrorExpression(syntax);
                    }

                    return new BoundMemberAccessExpression(syntax, field.Type, boundTarget, identifier, field);
                }

                // 6e-M22 C5+ 多播：事件不能作为值读取（CS0070 对齐）——仅允许语句级 +=/-= 与类内裸名触发。
                if (classType.GetEvent(identifier) is EventSymbol)
                {
                    _diagnostics.ReportEventNotAValue(syntax.IdentifierToken.Location, identifier);
                    return new BoundErrorExpression(syntax);
                }

                // 属性读：obj.Name → get_Name()
                var property = classType.GetProperty(identifier);
                if (property != null && property.Getter != null)
                {
                    if (!IsAccessibleMember(property.Getter.Visibility, property.Getter.ContainingClass!))
                    {
                        _diagnostics.ReportCannotAccessMember(syntax.IdentifierToken.Location, identifier, property.Getter.Visibility);
                        return new BoundErrorExpression(syntax);
                    }

                    return new BoundMemberCallExpression(syntax, boundTarget, property.Getter.Name, ImmutableArray<BoundExpression>.Empty, property.Type, property.Getter);
                }

                _diagnostics.ReportUnknownMember(syntax.IdentifierToken.Location, identifier, boundTarget.Type);
                return new BoundErrorExpression(syntax);
            }

            // 本轮仅支持数组/字符串的 Length（int 只读）；record/字符串成员访问后续里程碑
            if (boundTarget.Type.ElementType != null && identifier == "Length")
            {
                return new BoundMemberAccessExpression(syntax, TypeSymbol.Int32, boundTarget, identifier);
            }

            if (boundTarget.Type == TypeSymbol.String && identifier == "Length")
            {
                return new BoundMemberAccessExpression(syntax, TypeSymbol.Int32, boundTarget, identifier);
            }

            _diagnostics.ReportUnknownMember(syntax.IdentifierToken.Location, identifier, boundTarget.Type);
            return new BoundErrorExpression(syntax);
        }

        private BoundExpression BindMemberCallExpression(MemberCallExpressionSyntax syntax)
        {
            var identifier = syntax.IdentifierToken.Text;

            // 泛型显式实参（6e-M22 C1）：成员/类静态/命名空间限定三路实例化调用
            if (syntax.TypeArguments != null)
            {
                return BindGenericMemberMethodCall(syntax);
            }

            // 命名空间限定函数调用：System.Math.Max(...) / using System; + Math.Max(...)
            // 先于类静态方法路径（避免 System.Math.Max 被 .NET 真实类型劫持）
            if (TryBindNamespaceFunctionCall(syntax, identifier, out var namespaceCall))
            {
                return namespaceCall;
            }

            // 静态方法调用：MathHelpers.Square(2) / My.App.Utils.Square(2) / System.Math.Max(3,5)（target 是类型名，可为点号全名/别名）
            // 6e-M18：按参数类型解析重载（GetMethods 取全部同名静态方法）。
            if (ResolveDottedTypeName(syntax.Expression) is string staticTypeName &&
                LookupType(staticTypeName) is ClassTypeSymbol staticType)
            {
                var staticCandidates = staticType.GetMethods(identifier)
                    .Where(m => m.IsStatic && IsAccessibleMember(m.Visibility, staticType))
                    .ToImmutableArray();

                if (!staticCandidates.IsEmpty)
                {
                    var staticArguments = ImmutableArray.CreateBuilder<BoundExpression>();
                    foreach (var argument in syntax.Arguments)
                    {
                        staticArguments.Add(BindExpression(argument));
                    }

                    var staticMethod = ResolveMemberOverload(syntax.IdentifierToken.Location, identifier, staticCandidates, staticArguments.ToImmutable());
                    if (staticMethod == null)
                    {
                        return new BoundErrorExpression(syntax);
                    }

                    var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                    for (var i = 0; i < staticArguments.Count; i++)
                    {
                        arguments.Add(BindConversion(syntax.Arguments[i].Location, staticArguments[i], staticMethod.Parameters[i].Type));
                    }

                    return new BoundMemberCallExpression(syntax, new BoundStaticTypeExpression(syntax.Expression, staticType), identifier, arguments.ToImmutable(), staticMethod.ReturnType, staticMethod);
                }
            }

            var boundExpression = BindExpression(syntax.Expression);

            if (boundExpression.Type == TypeSymbol.Error)
            {
                return new BoundErrorExpression(syntax);
            }

            var boundArguments = ImmutableArray.CreateBuilder<BoundExpression>();
            foreach (var argument in syntax.Arguments)
            {
                boundArguments.Add(BindExpression(argument));
            }

            if (boundExpression.Type is ClassTypeSymbol classType)
            {
                var method = classType.GetMethod(identifier);
                if (method != null)
                {
                    if (!IsAccessibleMember(method.Visibility, method.ContainingClass!))
                    {
                        _diagnostics.ReportCannotAccessMember(syntax.IdentifierToken.Location, identifier, method.Visibility);
                        return new BoundErrorExpression(syntax);
                    }

                    if (method.Parameters.Length != syntax.Arguments.Count)
                    {
                        _diagnostics.ReportWrongArgumentCount(syntax.IdentifierToken.Location, identifier, method.Parameters.Length, syntax.Arguments.Count);
                        return new BoundErrorExpression(syntax);
                    }

                    var isBase = boundExpression is BoundBaseExpression;

                    var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                    for (var i = 0; i < boundArguments.Count; i++)
                    {
                        arguments.Add(BindConversion(syntax.Arguments[i].Location, boundArguments[i], method.Parameters[i].Type));
                    }

                    return new BoundMemberCallExpression(syntax, boundExpression, identifier, arguments.ToImmutable(), method.ReturnType, method, isBase);
                }

                // 6e-M22 C5+ 多播：事件不可作为值调用（CS0070 对齐）——触发仅限声明类内裸名。
                if (classType.GetEvent(identifier) is EventSymbol)
                {
                    _diagnostics.ReportEventNotAValue(syntax.IdentifierToken.Location, identifier);
                    return new BoundErrorExpression(syntax);
                }

                // 函数值字段间接调用（6e-M22 C4）：`obj.handler(x)` —— 实例字段持有函数值
                var functionField = classType.GetField(identifier);
                if (functionField?.Type is FunctionTypeSymbol fieldFunction)
                {
                    if (!IsAccessibleMember(functionField.Visibility, classType))
                    {
                        _diagnostics.ReportCannotAccessMember(syntax.IdentifierToken.Location, identifier, functionField.Visibility);
                        return new BoundErrorExpression(syntax);
                    }

                    var fieldCallee = new BoundMemberAccessExpression(syntax.Expression, functionField.Type, boundExpression, identifier, functionField);
                    return BindFunctionValueInvocation(syntax.IdentifierToken.Location, identifier, syntax.Arguments, fieldCallee, fieldFunction);
                }

                _diagnostics.ReportUnknownMember(syntax.IdentifierToken.Location, identifier, boundExpression.Type);
                return new BoundErrorExpression(syntax);
            }

            if (boundExpression.Type == TypeSymbol.String && identifier == "substring")
            {
                if (syntax.Arguments.Count != 2)
                {
                    _diagnostics.ReportWrongArgumentCount(syntax.IdentifierToken.Location, identifier, 2, syntax.Arguments.Count);
                    return new BoundErrorExpression(syntax);
                }

                var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                for (var i = 0; i < 2; i++)
                {
                    arguments.Add(BindConversion(syntax.Arguments[i].Location, boundArguments[i], TypeSymbol.Int32));
                }

                return new BoundMemberCallExpression(syntax, boundExpression, identifier, arguments.ToImmutable(), TypeSymbol.String);
            }

            // 6e-M19 M2-b：facade 路由——基元/string receiver 的实例调用绑定到 facade 类（声明侧已降级静态）
            var demoted = TryBindFacadeMemberCall(syntax, identifier, boundExpression, boundArguments.ToImmutable());
            if (demoted != null)
            {
                return demoted;
            }

            // 6e-M19 M2-c：Object 成员面回退——基元/string/any receiver 的 Object 方法
            // （ToString/GetHashCode/Equals/GetType；facade 同名方法优先，未命中才落到这里）
            var objectFace = TryBindObjectFaceMemberCall(syntax, identifier, boundExpression, boundArguments.ToImmutable());
            if (objectFace != null)
            {
                return objectFace;
            }

            _diagnostics.ReportUnknownMember(syntax.IdentifierToken.Location, identifier, boundExpression.Type);
            return new BoundErrorExpression(syntax);
        }

        /// <summary>
        /// 6e-M19 M2-c：Object 成员面回退绑定。receiver 非 ClassTypeSymbol（基元/string/any）时查
        /// SystemObject 单例的实例方法（虚四方法），receiver 保持表达式形状（三后端按 BuiltinKind 分发，
        /// 值类型装箱由发射器处理）。用户类 receiver 走上方 GetMethod 沿链路径，不经此处。
        /// </summary>
        private BoundExpression? TryBindObjectFaceMemberCall(
            MemberCallExpressionSyntax syntax,
            string identifier,
            BoundExpression receiver,
            ImmutableArray<BoundExpression> arguments)
        {
            if (receiver.Type == TypeSymbol.Void || receiver.Type == TypeSymbol.Error)
            {
                return null;
            }

            var candidates = ClassTypeSymbol.SystemObject.GetMethods(identifier)
                .Where(m => !m.IsStatic && m.BuiltinKind != null && IsAccessibleMember(m.Visibility, ClassTypeSymbol.SystemObject))
                .ToImmutableArray();
            if (candidates.IsEmpty)
            {
                return null;
            }

            var method = ResolveMemberOverload(syntax.IdentifierToken.Location, identifier, candidates, arguments);
            if (method == null)
            {
                return new BoundErrorExpression(syntax);
            }

            if (method.Parameters.Length != arguments.Length)
            {
                _diagnostics.ReportWrongArgumentCount(syntax.IdentifierToken.Location, identifier, method.Parameters.Length, arguments.Length);
                return new BoundErrorExpression(syntax);
            }

            var converted = ImmutableArray.CreateBuilder<BoundExpression>(arguments.Length);
            for (var i = 0; i < arguments.Length; i++)
            {
                // 参数类型 any：非 void 值隐式装箱/引用转换（Conversion.Classify）
                converted.Add(BindConversion(syntax.Arguments[i].Location, arguments[i], method.Parameters[i].Type));
            }

            return new BoundMemberCallExpression(syntax, receiver, identifier, converted.ToImmutable(), method.ReturnType, method);
        }

        /// <summary>
        /// 6e-M19 M2-b：facade 成员路由。receiver 为基元/string 时查找对应 facade 类（System.Int32 等，
        /// stdlib cod 注入）的方法（声明侧已降级为静态、首参 this），receiver 前置为首参完成静态容器
        /// 调用绑定，三后端零特判发射。未命中返回 null（外层继续报 UnknownMember）。
        /// </summary>
        private BoundExpression? TryBindFacadeMemberCall(
            MemberCallExpressionSyntax syntax,
            string identifier,
            BoundExpression receiver,
            ImmutableArray<BoundExpression> arguments)
        {
            var facadeClass = ResolveFacadeClass(receiver.Type);
            if (facadeClass == null)
            {
                return null;
            }

            var candidates = facadeClass.GetMethods(identifier)
                .Where(m => m.IsStatic && !m.IsConstructor && IsAccessibleMember(m.Visibility, facadeClass))
                .ToImmutableArray();
            if (candidates.IsEmpty)
            {
                return null;
            }

            var demotedArguments = ImmutableArray.CreateBuilder<BoundExpression>(arguments.Length + 1);
            demotedArguments.Add(receiver);
            foreach (var argument in arguments)
            {
                demotedArguments.Add(argument);
            }

            var method = ResolveMemberOverload(syntax.IdentifierToken.Location, identifier, candidates, demotedArguments.ToImmutable());
            if (method == null)
            {
                return new BoundErrorExpression(syntax);
            }

            var boundArguments = ImmutableArray.CreateBuilder<BoundExpression>(method.Parameters.Length);
            boundArguments.Add(BindConversion(syntax.Expression.Location, receiver, method.Parameters[0].Type));
            for (var i = 0; i < arguments.Length; i++)
            {
                boundArguments.Add(BindConversion(syntax.Arguments[i].Location, arguments[i], method.Parameters[i + 1].Type));
            }

            // 6e-M19 M2-b：走 BoundCallExpression（顶层静态调用形状）——与 .cod 库函数消费同路径，
            // 规避 MemberCall 静态分支对含类归属符号的发射差异
            return new BoundCallExpression(syntax, method, boundArguments.ToImmutable());
        }

        /// <summary>receiver 类型 → facade 类（stdlib cod 注入；全名解析优先，cod 库直查兜底）。</summary>
        private ClassTypeSymbol? ResolveFacadeClass(TypeSymbol receiverType)
        {
            var fullName = FacadeNameOfType(receiverType);
            if (fullName == null)
            {
                return null;
            }

            // 全名映射表为准（cod 注入类不带序列化标记；声明侧/注入侧均已补齐，此处双保险）
            if (!FacadeTargets.ContainsKey(fullName))
            {
                return null;
            }

            if (LookupType(fullName) is ClassTypeSymbol viaLookup)
            {
                return viaLookup;
            }

            foreach (var library in _codLibraries)
            {
                foreach (var candidate in library.Classes)
                {
                    if (candidate.FullName == fullName)
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        /// <summary>命名空间限定函数调用解析：`System.Math.Max(...)`（精确前缀）或 `using System;` + `Math.Max(...)`（using 前缀）。</summary>
        private bool TryBindNamespaceFunctionCall(MemberCallExpressionSyntax syntax, string identifier, out BoundExpression result)
        {
            result = null!;

            if (!(ResolveDottedTypeName(syntax.Expression) is string prefix) || prefix.Length == 0)
            {
                return false;
            }

            var candidates = ResolveDottedFunctionCandidates(prefix, identifier);

            if (candidates == null)
            {
                return false;
            }

            var boundArguments = ImmutableArray.CreateBuilder<BoundExpression>();
            foreach (var argument in syntax.Arguments)
            {
                boundArguments.Add(BindExpression(argument));
            }

            var function = ResolveMemberOverload(syntax.IdentifierToken.Location, identifier, candidates.Value, boundArguments.ToImmutable());
            if (function == null)
            {
                result = new BoundErrorExpression(syntax);
                return true;
            }

            for (var i = 0; i < syntax.Arguments.Count; i++)
            {
                boundArguments[i] = BindArgumentConversion(syntax.Arguments[i].Location, boundArguments[i], function.Parameters[i]);
            }

            result = new BoundCallExpression(syntax, function, boundArguments.ToImmutable());
            return true;
        }

        /// <summary>
        /// 点号前缀 → 函数候选解析（6e-M22 C1 自 TryBindNamespaceFunctionCall 抽取共用）：
        /// using 别名目标（类静态/命名空间函数）→ 直接命名空间 → using 命名空间扩展。null = 前缀非函数形态。
        /// </summary>
        private ImmutableArray<FunctionSymbol>? ResolveDottedFunctionCandidates(string prefix, string identifier)
        {
            ImmutableArray<FunctionSymbol>? candidates;

            // 别名前缀（6e-M18）：`using Con = System.Console;` + `Con.WriteLine(...)` → 类静态方法 / 命名空间函数
            if (_usingAliases.TryGetValue(prefix, out var aliasTarget))
            {
                candidates = LookupUsingStaticMethods(aliasTarget, identifier)
                             ?? _scope.TryLookupNamespaceFunctions(aliasTarget, identifier);
            }
            else
            {
                candidates = _scope.TryLookupNamespaceFunctions(prefix, identifier);
            }

            if (candidates == null)
            {
                foreach (var ns in _usingNamespaces)
                {
                    var full = ns.Length == 0 ? prefix : ns + "." + prefix;
                    var usingCandidates = _scope.TryLookupNamespaceFunctions(full, identifier);
                    if (usingCandidates != null)
                    {
                        candidates = usingCandidates;
                        break;
                    }
                }
            }

            return candidates;
        }

        /// <summary>`using static <类>`：取目标类的静态方法候选（含访问性；目标非类返回 null）。</summary>
        private ImmutableArray<FunctionSymbol>? LookupUsingStaticMethods(string target, string identifier)
        {
            if (LookupType(target) is not ClassTypeSymbol cls)
            {
                return null;
            }

            var methods = cls.Methods
                .Where(m => m.Name == identifier && m.IsStatic && IsAccessibleMember(m.Visibility, cls))
                .ToImmutableArray();

            return methods.Length == 0 ? null : methods;
        }

        private BoundExpression BindUnaryExpression(UnaryExpressionSyntax syntax)
        {
            if (syntax.OperatorToken.Kind == SyntaxKind.PlusPlusToken ||
                syntax.OperatorToken.Kind == SyntaxKind.MinusMinusToken)
            {
                return BindIncrementOrDecrement(syntax, syntax.Operand, syntax.OperatorToken);
            }

            var boundOperand = BindExpression(syntax.Operand);

            if (boundOperand.Type == TypeSymbol.Error)
            {
                return new BoundErrorExpression(syntax);
            }

            var boundOperator = BoundUnaryOperator.Bind(syntax.OperatorToken.Kind, boundOperand.Type);

            if (boundOperator == null)
            {
                _diagnostics.ReportUndefinedUnaryOperator(syntax.OperatorToken.Location, syntax.OperatorToken.Text, boundOperand.Type);
                return new BoundErrorExpression(syntax);
            }

            return new BoundUnaryExpression(syntax, boundOperator, boundOperand);
        }

        private BoundExpression BindBinaryExpression(BinaryExpressionSyntax syntax)
        {
            var boundLeft = BindExpression(syntax.Left);
            var boundRight = BindExpression(syntax.Right);
            var operatorKind = syntax.OperatorToken.Kind;
            var boundOperator = BoundBinaryOperator.Bind(operatorKind, boundLeft.Type, boundRight.Type);

            if (boundOperator == null && boundLeft.Type != TypeSymbol.Error && boundRight.Type != TypeSymbol.Error &&
                IsNumeric(boundLeft.Type) && IsNumeric(boundRight.Type))
            {
                // 6e-M21 Phase 1：二元数值提升——先求公共计算类型，两侧隐式归一后再查表
                var commonType = GetBinaryNumericResultType(boundLeft.Type, boundRight.Type, operatorKind);
                if (commonType != null)
                {
                    // 两侧统一归一到公共计算类型（移位计数同样提升，与各后端既有移位语义一致）
                    boundLeft = BindConversion(boundLeft.Syntax.Location, boundLeft, commonType, allowExplicit: false);
                    boundRight = BindConversion(boundRight.Syntax.Location, boundRight, commonType, allowExplicit: false);

                    boundOperator = BoundBinaryOperator.Bind(operatorKind, boundLeft.Type, boundRight.Type);
                }
            }

            if (boundLeft.Type == TypeSymbol.Error || boundRight.Type == TypeSymbol.Error)
            {
                return new BoundErrorExpression(syntax);
            }

            if (boundOperator == null)
            {
                _diagnostics.ReportUndefinedBinaryOperator(syntax.OperatorToken.Location, syntax.OperatorToken.Text, boundLeft.Type, boundRight.Type);
                return new BoundErrorExpression(syntax);
            }

            return new BoundBinaryExpression(syntax, boundLeft, boundOperator, boundRight);
        }

        /// <summary>
        /// 6e-M21 Phase 1：二元数值公共计算类型（无损提升优先）。
        /// 浮点参与 → 更宽浮点方；纯整数 → 同符号取更宽；异号同宽 → 双倍宽有符号；
        /// 异号异宽 → 值域能覆盖者（signed 更宽取 signed，unsigned 更宽取 unsigned）。
        /// i64+u64 无 128 位支撑 → null（报运算符未定义）。移位结果 = 左操作数提升类型（小整数→i32），
        /// 计数随后归一到同一公共类型。
        /// </summary>
        private static TypeSymbol? GetBinaryNumericResultType(TypeSymbol left, TypeSymbol right, SyntaxKind operatorKind)
        {
            var raw = GetRawBinaryNumericResultType(left, right, operatorKind);
            if (raw == null)
            {
                return null;
            }

            // 统一归一化：任何落在 <32 位域的整数结果升到 32 位（运算符表仅注册 32/64 位算术）
            if (raw.IsInteger && !raw.IsPlaceholder128 && raw.BitWidth < 32 &&
                operatorKind != SyntaxKind.ShiftLeftToken && operatorKind != SyntaxKind.ShiftRightToken)
            {
                return raw.IsSigned ? TypeSymbol.Int32 : TypeSymbol.UInt32;
            }

            return raw;
        }

        private static TypeSymbol? GetRawBinaryNumericResultType(TypeSymbol left, TypeSymbol right, SyntaxKind operatorKind)
        {
            if (operatorKind == SyntaxKind.ShiftLeftToken || operatorKind == SyntaxKind.ShiftRightToken)
            {
                return left.IsInteger && left.BitWidth < 32 ? TypeSymbol.Int32 : left;
            }

            if (left.IsFloat || right.IsFloat)
            {
                if (left.IsFloat && right.IsFloat)
                {
                    return left.BitWidth >= right.BitWidth ? left : right;
                }

                return left.IsFloat ? left : right;
            }

            if (left == right)
            {
                // 同型窄整型先升 32 位（C# 先升后算同构），其余保持
                if (left.IsInteger && !left.IsPlaceholder128 && left.BitWidth < 32)
                {
                    return left.IsSigned ? TypeSymbol.Int32 : TypeSymbol.UInt32;
                }

                return left;
            }

            if (left.IsSigned == right.IsSigned)
            {
                // C# 同构：位宽 <32 的同符号整数二元先升 32 位，避免 i16*i16 类中间截断
                if (left.BitWidth < 32 && right.BitWidth < 32)
                {
                    return left.IsSigned ? TypeSymbol.Int32 : TypeSymbol.UInt32;
                }

                return left.BitWidth >= right.BitWidth ? left : right;
            }

            var signed = left.IsSigned ? left : right;
            var unsigned = left.IsSigned ? right : left;

            if (signed.BitWidth > unsigned.BitWidth)
            {
                // 有符号更宽：值域完整覆盖无符号方
                return signed;
            }

            if (signed.BitWidth == unsigned.BitWidth)
            {
                // 同宽异号：升双倍宽有符号（i8+u8→i16 / i16+u16→i32 / i32+u32→i64）；64 位对无 128 支撑 → null
                return SignedTypeOfWidth(signed.BitWidth * 2);
            }

            // 无符号更宽：值域覆盖有符号方的非负域，取无符号（与 C# ulong 行为一致）
            return unsigned;
        }

        private static TypeSymbol? SignedTypeOfWidth(int bits) => bits switch
        {
            8 => TypeSymbol.Int8,
            16 => TypeSymbol.Int16,
            32 => TypeSymbol.Int32,
            64 => TypeSymbol.Int64,
            _ => null,
        };

        private static TypeSymbol? UnsignedTypeOfWidth(int bits) => bits switch
        {
            8 => TypeSymbol.UInt8,
            16 => TypeSymbol.UInt16,
            32 => TypeSymbol.UInt32,
            64 => TypeSymbol.UInt64,
            _ => null,
        };

        private BoundExpression BindConditionalExpression(ConditionalExpressionSyntax syntax)
        {
            var condition = BindExpression(syntax.Condition, TypeSymbol.Boolean);
            var whenTrue = BindExpression(syntax.WhenTrue);
            var whenFalse = BindExpression(syntax.WhenFalse);

            TypeSymbol type;
            if (whenTrue.Type == whenFalse.Type)
            {
                type = whenTrue.Type;
            }
            else if (whenTrue.Type == TypeSymbol.Error || whenFalse.Type == TypeSymbol.Error)
            {
                type = whenTrue.Type == TypeSymbol.Error ? whenFalse.Type : whenTrue.Type;
            }
            else if (IsNumeric(whenTrue.Type) && IsNumeric(whenFalse.Type))
            {
                if (Conversion.Classify(whenTrue.Type, whenFalse.Type).IsImplicit)
                {
                    type = whenFalse.Type;
                }
                else if (Conversion.Classify(whenFalse.Type, whenTrue.Type).IsImplicit)
                {
                    type = whenTrue.Type;
                }
                else
                {
                    _diagnostics.ReportCannotConvert(syntax.WhenFalse.Location, whenFalse.Type, whenTrue.Type);
                    return new BoundErrorExpression(syntax);
                }
            }
            else if (whenTrue.Type == TypeSymbol.Null && IsNullableReferenceType(whenFalse.Type))
            {
                // 6e-M19 M5-a：cond ? null : obj → obj 类型（null 分支随后隐式转换）
                type = whenFalse.Type;
            }
            else if (whenFalse.Type == TypeSymbol.Null && IsNullableReferenceType(whenTrue.Type))
            {
                type = whenTrue.Type;
            }
            else
            {
                _diagnostics.ReportCannotConvert(syntax.WhenFalse.Location, whenFalse.Type, whenTrue.Type);
                return new BoundErrorExpression(syntax);
            }

            var convertedWhenTrue = BindConversion(syntax.WhenTrue.Location, whenTrue, type);
            var convertedWhenFalse = BindConversion(syntax.WhenFalse.Location, whenFalse, type);

            return new BoundConditionalExpression(syntax, condition, convertedWhenTrue, convertedWhenFalse);
        }

        private BoundExpression BindCallExpression(CallExpressionSyntax syntax)
        {
            // 泛型方法显式实参调用（6e-M20）：`Swap<int>(a, b)` → 实例化具体方法
            if (syntax.TypeArguments != null)
            {
                return BindGenericMethodCall(syntax);
            }

            // 优先于强制转换简写：若标识符是已知函数/方法，按调用解析，
            // 避免与类型名同名（如 .NET System.HashCode）时 `HashCode(x)` 被误判为 (HashCode)x 转换
            if (!IsFunctionName(syntax.Identifier.Text) &&
                syntax.Arguments.Count == 1 &&
                LookupType(syntax.Identifier.Text) is TypeSymbol type)
            {
                return BindConversion(syntax.Arguments[0], type, allowExplicit: true);
            }

            // 函数值间接调用（6e-M22 C4/D-B）：`f(x)` —— 标识符解析为函数类型或 delegate 类型的变量/参数
            if (_scope.TryLookupSymbol(syntax.Identifier.Text) is VariableSymbol calleeVariable)
            {
                FunctionTypeSymbol? calleeFnType = calleeVariable.Type switch
                {
                    FunctionTypeSymbol ft => ft,
                    ClassTypeSymbol { IsDelegateClass: true } dc => dc.GetDelegateSignature(),
                    _ => null,
                };

                if (calleeFnType != null)
                {
                    return BindFunctionValueInvocation(
                        syntax.Identifier.Location,
                        syntax.Identifier.Text,
                        syntax.Arguments,
                        new BoundVariableExpression(syntax, calleeVariable),
                        calleeFnType);
                }
            }

            // 6e-M22 C5+ 多播：类内触发 `e(args)` 已在语句级拦截（BindEventRaise）。
            // 非语句位置的事件名调用走通用查找，报 not-a-function（事件不可作值）。

            var boundArguments = ImmutableArray.CreateBuilder<BoundExpression>();

            foreach (var argument in syntax.Arguments)
            {
                var boundArgument = BindExpression(argument);
                boundArguments.Add(boundArgument);
            }

            // 候选：裸函数（含重载）→ using 命名空间函数 → using static 类静态方法 → 类内方法
            var candidates = _scope.TryLookupFunctions(syntax.Identifier.Text);
            if (candidates is { Length: 0 })
            {
                // 被同名非函数符号遮蔽（变量/类型）：报 not-a-function
                _diagnostics.ReportNotAFunction(syntax.Identifier.Location, syntax.Identifier.Text);
                return new BoundErrorExpression(syntax);
            }

            if (candidates == null)
            {
                foreach (var ns in _usingNamespaces)
                {
                    var usingCandidates = _scope.TryLookupNamespaceFunctions(ns, syntax.Identifier.Text);
                    if (usingCandidates != null)
                    {
                        candidates = usingCandidates;
                        break;
                    }
                }
            }

            // `using static <类>`：导入类静态方法为裸名（6e-M18，仅类，C# 同构）
            if (candidates == null)
            {
                foreach (var target in _usingStatics)
                {
                    var usingStatic = LookupUsingStaticMethods(target, syntax.Identifier.Text);
                    if (usingStatic != null)
                    {
                        candidates = usingStatic;
                        break;
                    }
                }
            }

            // 类方法内：裸方法调用解析为本类方法（this.Method()；6e-M18 按参数类型解析重载）
            if (candidates == null && _currentClass != null)
            {
                var classMethods = _currentClass.GetMethods(syntax.Identifier.Text)
                    .Where(m => IsAccessibleMember(m.Visibility, _currentClass))
                    .ToImmutableArray();
                if (!classMethods.IsEmpty)
                {
                    var classMethod = ResolveMemberOverload(syntax.Identifier.Location, syntax.Identifier.Text, classMethods, boundArguments.ToImmutable());
                    if (classMethod != null)
                    {
                        return BindMemberCall(syntax, new BoundThisExpression(syntax, _currentClass), classMethod);
                    }
                }
            }

            if (candidates == null)
            {
                _diagnostics.ReportUndefinedFunction(syntax.Identifier.Location, syntax.Identifier.Text);
                return new BoundErrorExpression(syntax);
            }

            var function = ResolveOverload(syntax, candidates.Value, boundArguments.ToImmutable());
            if (function == null)
            {
                return new BoundErrorExpression(syntax);
            }

            for (var i = 0; i < syntax.Arguments.Count; i++)
            {
                var argumentLocation = syntax.Arguments[i].Location;
                var argument = boundArguments[i];
                var parameter = function.Parameters[i];

                boundArguments[i] = BindArgumentConversion(argumentLocation, argument, parameter);
            }

            return new BoundCallExpression(syntax, function, boundArguments.ToImmutable());
        }

        /// <summary>判断标识符是否为可调用函数/方法名（裸调用 `Foo(args)` 应走调用而非转换简写；避免与类型名同名冲突）。</summary>
        private bool IsFunctionName(string name)
        {
            if (_scope.TryLookupFunctions(name) is { Length: > 0 })
            {
                return true;
            }

            if (_currentClass != null && !_currentClass.GetMethods(name).IsEmpty)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 重载解析：参数量过滤 → 隐式转换可行性（Any 通吃，显式不参与）→ 计分（identity&lt;implicit）选最佳；
        /// 无可行报无匹配重载；并列最低报歧义。
        /// </summary>
        private FunctionSymbol? ResolveOverload(CallExpressionSyntax syntax, ImmutableArray<FunctionSymbol> candidates, ImmutableArray<BoundExpression> arguments)
        {
            if (candidates.Length == 1)
            {
                var only = candidates[0];
                if (arguments.Length != only.Parameters.Length)
                {
                    ReportArgumentCountMismatch(syntax, only);
                    return null;
                }

                return only;
            }

            return ResolveOverloadByScore(syntax.Identifier.Location, syntax.Identifier.Text, candidates, arguments);
        }

        private FunctionSymbol? ResolveMemberOverload(TextLocation location, string name, ImmutableArray<FunctionSymbol> candidates, ImmutableArray<BoundExpression> arguments)
        {
            if (candidates.Length == 1)
            {
                var only = candidates[0];
                if (arguments.Length != only.Parameters.Length)
                {
                    _diagnostics.ReportWrongArgumentCount(location, name, only.Parameters.Length, arguments.Length);
                    return null;
                }

                return only;
            }

            return ResolveOverloadByScore(location, name, candidates, arguments);
        }

        private FunctionSymbol? ResolveOverloadByScore(TextLocation location, string name, ImmutableArray<FunctionSymbol> candidates, ImmutableArray<BoundExpression> arguments)
        {
            var viable = new List<(FunctionSymbol Function, int Score)>();
            foreach (var candidate in candidates)
            {
                if (candidate.Parameters.Length != arguments.Length)
                {
                    continue;
                }

                var score = 0;
                var ok = true;
                for (var i = 0; i < arguments.Length; i++)
                {
                    // byref 对应过滤（6e-M23 R3）：修饰符不匹配的候选直接出局（f(i32) 与 f(out i32) 不构成歧义）
                    if (candidate.Parameters[i].IsByRef != arguments[i] is BoundByRefArgument)
                    {
                        ok = false;
                        break;
                    }

                    if (arguments[i] is BoundByRefArgument wrapped &&
                        wrapped.IsRef != candidate.Parameters[i].IsRef)
                    {
                        ok = false;
                        break;
                    }

                    var conversion = Conversion.Classify(arguments[i].Type, candidate.Parameters[i].Type);
                    if (!conversion.Exists || !conversion.IsImplicit)
                    {
                        ok = false;
                        break;
                    }

                    if (!conversion.IsIdentity)
                    {
                        score++;
                    }
                }

                if (ok)
                {
                    viable.Add((candidate, score));
                }
            }

            if (viable.Count == 0)
            {
                _diagnostics.ReportNoMatchingOverload(location, name);
                return null;
            }

            var minScore = viable.Min(v => v.Score);
            var best = viable.Where(v => v.Score == minScore).ToList();
            if (best.Count == 1)
            {
                return best[0].Function;
            }

            _diagnostics.ReportAmbiguousInvocation(location, name);
            return null;
        }

        private void ReportArgumentCountMismatch(CallExpressionSyntax syntax, FunctionSymbol function)
        {
            TextSpan span;
            if (syntax.Arguments.Count > function.Parameters.Length)
            {
                SyntaxNode firstExceedingNode;
                if (function.Parameters.Length > 0)
                {
                    firstExceedingNode = syntax.Arguments.GetSeparator(function.Parameters.Length - 1);
                }
                else
                {
                    firstExceedingNode = syntax.Arguments[0];
                }

                var lastExceedingArgument = syntax.Arguments[syntax.Arguments.Count - 1];
                span = TextSpan.FromBounds(firstExceedingNode.Span.Start, lastExceedingArgument.Span.End);
            }
            else
            {
                span = syntax.CloseParenthesisToken.Span;
            }

            var location = new TextLocation(syntax.SyntaxTree.Text, span);
            _diagnostics.ReportWrongArgumentCount(location, function.Name, function.Parameters.Length, syntax.Arguments.Count);
        }

        private BoundExpression BindMemberCall(CallExpressionSyntax syntax, BoundExpression target, FunctionSymbol method)
        {
            var boundArguments = ImmutableArray.CreateBuilder<BoundExpression>();

            foreach (var argument in syntax.Arguments)
            {
                boundArguments.Add(BindExpression(argument));
            }

            if (syntax.Arguments.Count != method.Parameters.Length)
            {
                _diagnostics.ReportWrongArgumentCount(syntax.Identifier.Location, method.Name, method.Parameters.Length, syntax.Arguments.Count);
                return new BoundErrorExpression(syntax);
            }

            for (var i = 0; i < syntax.Arguments.Count; i++)
            {
                boundArguments[i] = BindArgumentConversion(syntax.Arguments[i].Location, boundArguments[i], method.Parameters[i]);
            }

            return new BoundMemberCallExpression(syntax, target, method.Name, boundArguments.ToImmutable(), method.ReturnType, method);
        }

        private BoundExpression BindConversion(ExpressionSyntax syntax, TypeSymbol type, bool allowExplicit = false)
        {
            // 期望类型下推（6e-M22 C4）：lambda 字面量在目标函数类型位置按目标签名提升
            if (type is FunctionTypeSymbol expectedFunction && syntax.Kind == SyntaxKind.LambdaExpression)
            {
                var lambdaValue = BindLambdaExpression((LambdaExpressionSyntax)syntax, expectedFunction);
                if (lambdaValue.Type != type && lambdaValue.Type != TypeSymbol.Error)
                {
                    _diagnostics.ReportCannotConvert(syntax.Location, lambdaValue.Type, type);
                    return new BoundErrorExpression(syntax);
                }

                return lambdaValue;
            }

            // 6e-M22 D-A：目标为 delegate 类时提取 Invoke 签名作为函数类型（复用函数值管道）
            if (type is ClassTypeSymbol { IsDelegateClass: true } delegateClass)
            {
                var delegateSignature = delegateClass.GetDelegateSignature();
                if (delegateSignature == null)
                {
                    _diagnostics.ReportError(syntax.Location, $"delegate 类 '{delegateClass.Name}' 缺少 Invoke 方法。");
                    return new BoundErrorExpression(syntax);
                }

                if (syntax.Kind == SyntaxKind.LambdaExpression)
                {
                    var lambdaValue = BindLambdaExpression((LambdaExpressionSyntax)syntax, delegateSignature);
                    if (lambdaValue.Type != delegateSignature && lambdaValue.Type != TypeSymbol.Error)
                    {
                        _diagnostics.ReportCannotConvert(syntax.Location, lambdaValue.Type, delegateSignature);
                        return new BoundErrorExpression(syntax);
                    }

                    return lambdaValue;
                }

                // 方法组/命名函数 → delegate 类型
                if (syntax.Kind == SyntaxKind.NameExpression)
                {
                    var asValue = TryBindNameAsFunctionValue((NameExpressionSyntax)syntax);
                    if (asValue != null)
                    {
                        if (asValue.Type != delegateSignature)
                        {
                            _diagnostics.ReportCannotConvert(syntax.Location, asValue.Type, delegateSignature);
                            return new BoundErrorExpression(syntax);
                        }

                        return asValue;
                    }
                }
            }

            // 方法组到函数类型的转换（6e-M22 C4）：命名方法/实例方法引用 → 一等函数值
            if (type is FunctionTypeSymbol functionTarget && syntax.Kind == SyntaxKind.NameExpression)
            {
                var asValue = TryBindNameAsFunctionValue((NameExpressionSyntax)syntax);
                if (asValue != null)
                {
                    if (asValue.Type != functionTarget)
                    {
                        _diagnostics.ReportCannotConvert(syntax.Location, asValue.Type, functionTarget);
                        return new BoundErrorExpression(syntax);
                    }

                    return asValue;
                }
            }

            var expression = BindExpression(syntax);

            return BindConversion(syntax.Location, expression, type, allowExplicit);
        }

        /// <summary>裸名 → 函数值尝试（6e-M22 C4）：恰一候选的非构造函数；否则 null 回落常规绑定。</summary>
        private BoundFunctionValueExpression? TryBindNameAsFunctionValue(NameExpressionSyntax syntax)
        {
            var name = syntax.IdentifierToken.Text;
            var candidates = _scope.TryLookupFunctions(name);
            if (candidates == null)
            {
                return null;
            }

            var functions = candidates.Value.Where(f => !f.IsConstructor).ToImmutableArray();
            if (functions.Length != 1)
            {
                return null;
            }

            return CreateFunctionValue(syntax, receiver: null, functions[0]) as BoundFunctionValueExpression;
        }

        /// <summary>函数值间接调用共享核心（6e-M22 C4）：元数校验 + 实参转换 + Invocation 节点。</summary>
        private BoundExpression BindFunctionValueInvocation(TextLocation errorLocation, string displayName, SeparatedSyntaxList<ExpressionSyntax> argumentSyntaxes, BoundExpression callee, FunctionTypeSymbol functionType)
        {
            if (functionType.ParameterTypes.Length != argumentSyntaxes.Count)
            {
                _diagnostics.ReportWrongArgumentCount(errorLocation, displayName, functionType.ParameterTypes.Length, argumentSyntaxes.Count);
                return new BoundErrorExpression(callee.Syntax!);
            }

            var boundArguments = ImmutableArray.CreateBuilder<BoundExpression>();
            for (var i = 0; i < argumentSyntaxes.Count; i++)
            {
                var boundArgument = BindExpression(argumentSyntaxes[i]);
                if (boundArgument is BoundByRefArgument)
                {
                    _diagnostics.ReportFunctionTypeByRefParameter(argumentSyntaxes[i].Location);
                    return new BoundErrorExpression(callee.Syntax!);
                }

                boundArguments.Add(BindConversion(argumentSyntaxes[i].Location, boundArgument, functionType.ParameterTypes[i]));
            }

            return new BoundInvocationExpression(callee.Syntax!, callee, boundArguments.ToImmutable(), functionType.ReturnType);
        }

        private BoundExpression BindConversion(TextLocation diagnosticLocation, BoundExpression expression, TypeSymbol type, bool allowExplicit = false)
        {
            // 6e-M22 D-A：delegate 类目标——函数值与 delegate 类型的结构兼容（同表示，类型身份编译期）
            if (type is ClassTypeSymbol { IsDelegateClass: true } delegateTarget &&
                expression.Type == delegateTarget.GetDelegateSignature())
            {
                return expression;
            }

            var conversion = Conversion.Classify(expression.Type, type);
            if (!conversion.Exists)
            {
                if (expression.Type != TypeSymbol.Error && type != TypeSymbol.Error)
                {
                    _diagnostics.ReportCannotConvert(diagnosticLocation, expression.Type, type);
                }

                return new BoundErrorExpression(expression.Syntax);
            }

            if (!allowExplicit && conversion.IsExplicit)
            {
                // 6e-M21 Phase 4：范围内整数常量允许隐式窄化（C# 同构：ushort x = 60000）。
                // 覆盖 i8/i16/u8/u16/u32；byte 沿用既有专属消息，其余走通用消息。
                if (IsNarrowIntegerTarget(type) && TryGetIntegerConstant(expression, out var constValue))
                {
                    if (!FitsInIntegerType(constValue, type))
                    {
                        if (type == TypeSymbol.UInt8)
                        {
                            _diagnostics.ReportByteConstantOutOfRange(diagnosticLocation, (int)constValue);
                        }
                        else
                        {
                            _diagnostics.ReportConstantOutOfRange(diagnosticLocation, constValue, type.Name);
                        }
                    }
                }
                else
                {
                    _diagnostics.ReportCannotConvertImplicitly(diagnosticLocation, expression.Type, type);
                }
            }

            if (conversion.IsIdentity)
            {
                return expression;
            }

            return new BoundConversionExpression(expression.Syntax, type, expression);
        }

        private VariableSymbol BindVariableDeclaration(SyntaxToken identifier, bool isReadOnly, TypeSymbol type, BoundConstant? constant = null)
        {
            var name = identifier.Text ?? "?";
            var declare = !identifier.IsMissing;
            var variable = _function == null
                ? (VariableSymbol)new GlobalVariableSymbol(name, isReadOnly, type, constant)
                : new LocalVariableSymbol(name, isReadOnly, type, constant);

            if (declare && !_scope.TryDeclareVariable(variable))
            {
                _diagnostics.ReportSymbolAlreadyDeclared(identifier.Location, name);
            }

            return variable;
        }

        private VariableSymbol? BindVariableReference(SyntaxToken identifierToken)
        {
            var name = identifierToken.Text;

            switch (_scope.TryLookupSymbol(name))
            {
                case VariableSymbol variable:
                    return variable;
                case null:
                    _diagnostics.ReportUndefinedVariable(identifierToken.Location, name);
                    return null;
                default:
                    _diagnostics.ReportNotAVariable(identifierToken.Location, name);
                    return null;
            }
        }

        private TypeSymbol? LookupType(string name)
        {
            var builtin = LookupBuiltinType(name);
            if (builtin != null)
            {
                return builtin;
            }

            // 单态化重绑：类型参数名 → 具体实参（6e-M20 Monomorphizer 注入）
            if (_typeArgumentsByName.TryGetValue(name, out var typeArgument))
            {
                return typeArgument;
            }

            // 泛型类型参数（6e-M20）：当前方法/声明上下文中的 T/U 优先（定义期"不透明类型"）
            if (_currentClass != null)
            {
                foreach (var typeParameter in _currentClass.TypeParameters)
                {
                    if (typeParameter.Name == name)
                    {
                        return typeParameter;
                    }
                }
            }

            if (_bindingClass != null)
            {
                foreach (var typeParameter in _bindingClass.TypeParameters)
                {
                    if (typeParameter.Name == name)
                    {
                        return typeParameter;
                    }
                }
            }

            // 泛型方法签名绑定上下文（6e-M20）
            foreach (var methodTypeParameter in _declaringMethodTypeParameters)
            {
                if (methodTypeParameter.Name == name)
                {
                    return methodTypeParameter;
                }
            }

            // using 别名（6e-M18）：`using Rt = System.Runtime;` + 类型位置 / Rt.StaticMethod()
            if (_usingAliases.TryGetValue(name, out var aliasTarget))
            {
                return LookupType(aliasTarget);
            }

            var lookup = _scope.TryLookupSymbol(name);
            if (lookup is TypeSymbol declaredType)
            {
                return declaredType;
            }

            // 6e-M19 M2-a：System.Object 内建单例（用户同名类已由上方 scope 命中短路；小写关键字与 C# 原名皆可）
            if (name is "object" or "Object")
            {
                return ClassTypeSymbol.SystemObject;
            }

            // 点号全名（`Foo.Bar.Point` / `Foo.Bar.Color`）：内部类/枚举按 FullName 匹配，或外部类型直查
            if (name.IndexOf('.') >= 0)
            {
                var fullNameClass = FindDeclaredClassByFullName(name);
                if (fullNameClass != null)
                {
                    return fullNameClass;
                }

                var fullNameEnum = FindDeclaredEnumByFullName(name);
                if (fullNameEnum != null)
                {
                    return fullNameEnum;
                }

                // 6e-M19 M2-a：System.Object / System.Type 内建（用户同名类优先）
                var systemType = ResolveBuiltInSystemType(name);
                if (systemType != null)
                {
                    return systemType;
                }

                return ExternalTypeResolver.TryResolve(name, _references);
            }

            // using 前缀：`using Foo.Bar;` 后 `LookupType("Point")` → 内部命名空间类/枚举 + 引用程序集
            foreach (var ns in _usingNamespaces)
            {
                var fullName = ns.Length == 0 ? name : ns + "." + name;
                var internalClass = FindDeclaredClassByFullName(fullName);
                if (internalClass != null)
                {
                    return internalClass;
                }

                var internalEnum = FindDeclaredEnumByFullName(fullName);
                if (internalEnum != null)
                {
                    return internalEnum;
                }

                var systemType = ResolveBuiltInSystemType(fullName);
                if (systemType != null)
                {
                    return systemType;
                }

                var externalType = ExternalTypeResolver.TryResolve(fullName, _references);
                if (externalType != null)
                {
                    return externalType;
                }
            }

            return null;
        }

        /// <summary>6e-M19 M2-b：facade 类全名 → 承载类型映射（null 值 = 自身，Object/Type facade）。</summary>
        private static readonly Dictionary<string, TypeSymbol?> FacadeTargets = new Dictionary<string, TypeSymbol?>
        {
            ["System.String"] = TypeSymbol.String,
            ["System.SByte"] = TypeSymbol.Int8,
            ["System.Int16"] = TypeSymbol.Int16,
            ["System.Int32"] = TypeSymbol.Int32,
            ["System.Int64"] = TypeSymbol.Int64,
            ["System.Byte"] = TypeSymbol.UInt8,
            ["System.UInt16"] = TypeSymbol.UInt16,
            ["System.UInt32"] = TypeSymbol.UInt32,
            ["System.UInt64"] = TypeSymbol.UInt64,
            ["System.Single"] = TypeSymbol.Float,
            ["System.Double"] = TypeSymbol.Double,
            ["System.Boolean"] = TypeSymbol.Boolean,
            ["System.Char"] = TypeSymbol.Char,
            ["System.Object"] = null,
            ["System.Type"] = null,
            ["System.Exception"] = null,
        };

        /// <summary>6e-M19 M2-b：facade 静态常量表（i32.MaxValue 等，编译期折叠为字面量）。</summary>
        private static readonly Dictionary<string, Dictionary<string, object>> FacadeConstants = new Dictionary<string, Dictionary<string, object>>
        {
            ["System.Int32"] = new Dictionary<string, object>
            {
                ["MaxValue"] = int.MaxValue,
                ["MinValue"] = int.MinValue,
            },
            ["System.Int64"] = new Dictionary<string, object>
            {
                ["MaxValue"] = long.MaxValue,
                ["MinValue"] = long.MinValue,
            },
            ["System.Byte"] = new Dictionary<string, object>
            {
                // 归一为 i32（u8 常量值域安全，且字面量发射器不识别 byte 装箱）
                ["MaxValue"] = (int)byte.MaxValue,
                ["MinValue"] = (int)byte.MinValue,
            },
            ["System.Double"] = new Dictionary<string, object>
            {
                ["MaxValue"] = double.MaxValue,
                ["MinValue"] = double.MinValue,
                ["Epsilon"] = double.Epsilon,
                ["NaN"] = double.NaN,
                ["PositiveInfinity"] = double.PositiveInfinity,
                ["NegativeInfinity"] = double.NegativeInfinity,
            },
            ["System.Boolean"] = new Dictionary<string, object>
            {
                ["TrueString"] = "True",
                ["FalseString"] = "False",
            },
            ["System.Int16"] = new Dictionary<string, object>
            {
                ["MaxValue"] = (short)32767,
                ["MinValue"] = (short)(-32768),
            },
            ["System.UInt16"] = new Dictionary<string, object>
            {
                ["MaxValue"] = (ushort)65535,
                ["MinValue"] = (ushort)0,
            },
            ["System.UInt32"] = new Dictionary<string, object>
            {
                ["MaxValue"] = (uint)4294967295,
                ["MinValue"] = (uint)0,
            },
            ["System.UInt64"] = new Dictionary<string, object>
            {
                ["MaxValue"] = ulong.MaxValue,
                ["MinValue"] = (ulong)0,
            },
            ["System.SByte"] = new Dictionary<string, object>
            {
                ["MaxValue"] = (sbyte)127,
                ["MinValue"] = (sbyte)(-128),
            },
            ["System.Char"] = new Dictionary<string, object>
            {
                ["MaxValue"] = (char)0xFFFF,
                ["MinValue"] = (char)0x0000,
            },
            ["System.Single"] = new Dictionary<string, object>
            {
                ["MaxValue"] = float.MaxValue,
                ["MinValue"] = float.MinValue,
                ["Epsilon"] = float.Epsilon,
                ["NaN"] = float.NaN,
                ["PositiveInfinity"] = float.PositiveInfinity,
                ["NegativeInfinity"] = float.NegativeInfinity,
            },
        };

        /// <summary>基元类型 → facade 全名（全基元集 + facade 类符号自身——dotted 类名形式解析产物）。</summary>
        private static string? FacadeNameOfType(TypeSymbol receiverType)
        {
            if (receiverType is ClassTypeSymbol classSymbol &&
                (classSymbol.IsFacadeClass || FacadeTargets.ContainsKey(classSymbol.FullName)))
            {
                return classSymbol.FullName;
            }

            if (receiverType == TypeSymbol.String) return "System.String";
            if (receiverType == TypeSymbol.Boolean) return "System.Boolean";
            if (receiverType == TypeSymbol.Char) return "System.Char";
            if (receiverType == TypeSymbol.Int8) return "System.SByte";
            if (receiverType == TypeSymbol.Int16) return "System.Int16";
            if (receiverType == TypeSymbol.Int32) return "System.Int32";
            if (receiverType == TypeSymbol.Int64) return "System.Int64";
            if (receiverType == TypeSymbol.UInt8) return "System.Byte";
            if (receiverType == TypeSymbol.UInt16) return "System.UInt16";
            if (receiverType == TypeSymbol.UInt32) return "System.UInt32";
            if (receiverType == TypeSymbol.UInt64) return "System.UInt64";
            if (receiverType == TypeSymbol.Float) return "System.Single";
            if (receiverType == TypeSymbol.Double) return "System.Double";
            return null;
        }

        /// <summary>6e-M19 M2-a：System.Object / System.Type 内建单例按名解析（裸 Type 不在此列，避免劫持 using 导入的同名类型）。</summary>
        private static TypeSymbol? ResolveBuiltInSystemType(string fullName)
        {
            switch (fullName)
            {
                case "object":
                case "Object":
                case "System.Object":
                    return ClassTypeSymbol.SystemObject;
                case "System.Type":
                    return ClassTypeSymbol.SystemType;
                default:
                    return null;
            }
        }

        /// <summary>
        /// 按方言解析内建类型名。CO 方言只认简写（i8/u8/.../f32/f64 + 128 占位），
        /// C# 方言只认原名（sbyte/byte/short/.../float/double）。两方言共享 bool/char/string/void/any。
        /// </summary>
        private TypeSymbol? LookupBuiltinType(string name)
        {
            switch (name)
            {
                case "any": return TypeSymbol.Any;
                case "bool": return TypeSymbol.Boolean;
                case "char": return TypeSymbol.Char;
                case "string": return TypeSymbol.String;
                case "void": return TypeSymbol.Void;
            }

            if (_dialect == LanguageDialect.CSharp)
            {
                switch (name)
                {
                    case "int": return TypeSymbol.Int32;
                    case "long": return TypeSymbol.Int64;
                    case "short": return TypeSymbol.Int16;
                    case "ushort": return TypeSymbol.UInt16;
                    case "uint": return TypeSymbol.UInt32;
                    case "ulong": return TypeSymbol.UInt64;
                    case "sbyte": return TypeSymbol.Int8;
                    case "byte": return TypeSymbol.UInt8;
                    case "float": return TypeSymbol.Float;
                    case "double": return TypeSymbol.Double;
                }
            }
            else
            {
                switch (name)
                {
                    case "i8": return TypeSymbol.Int8;
                    case "u8": return TypeSymbol.UInt8;
                    case "i16": return TypeSymbol.Int16;
                    case "u16": return TypeSymbol.UInt16;
                    case "i32": return TypeSymbol.Int32;
                    case "u32": return TypeSymbol.UInt32;
                    case "i64": return TypeSymbol.Int64;
                    case "u64": return TypeSymbol.UInt64;
                    case "f32": return TypeSymbol.Float;
                    case "f64": return TypeSymbol.Double;
                    case "i128": return TypeSymbol.Int128;
                    case "u128": return TypeSymbol.UInt128;
                    case "f128": return TypeSymbol.Float128;
                }
            }

            return null;
        }

        /// <summary>按全名（`Namespace.ClassName`）沿作用域链查找内部声明的类。</summary>
        private ClassTypeSymbol? FindDeclaredClassByFullName(string fullName)
        {
            for (var scope = _scope; scope != null; scope = scope.Parent)
            {
                foreach (var cls in scope.GetDeclaredClasses())
                {
                    if (cls.FullName == fullName)
                    {
                        return cls;
                    }
                }
            }

            return null;
        }

        /// <summary>按全名（`Namespace.EnumName`）沿作用域链查找内部声明的枚举。</summary>
        private EnumTypeSymbol? FindDeclaredEnumByFullName(string fullName)
        {
            for (var scope = _scope; scope != null; scope = scope.Parent)
            {
                foreach (var enumType in scope.GetDeclaredEnums())
                {
                    if (enumType.FullName == fullName)
                    {
                        return enumType;
                    }
                }
            }

            return null;
        }

        /// <summary>纯标识符成员链拍平成点号字符串（`Foo.Bar.Program`）；含调用/索引等非纯链返回 null。</summary>
        private static string? ResolveDottedTypeName(ExpressionSyntax expr)
        {
            if (expr is NameExpressionSyntax nameExpr)
            {
                return nameExpr.IdentifierToken.Text;
            }

            if (expr is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.IdentifierToken.Text.Length > 0)
            {
                var left = ResolveDottedTypeName(memberAccess.Expression);
                return left == null ? null : left + "." + memberAccess.IdentifierToken.Text;
            }

            return null;
        }

        /// <summary>限定入口解析：`ClassName.Method` / `Namespace.ClassName.Method` → 类静态方法。</summary>
        private static FunctionSymbol? ResolveQualifiedEntryPoint(Binder binder, string entryPointName, TextLocation location)
        {
            var lastDot = entryPointName.LastIndexOf('.');
            var className = entryPointName.Substring(0, lastDot);
            var methodName = entryPointName.Substring(lastDot + 1);

            var classMatches = new List<ClassTypeSymbol>();
            for (var scope = binder._scope; scope != null; scope = scope.Parent)
            {
                foreach (var cls in scope.GetDeclaredClasses())
                {
                    if (cls.Name == className || cls.FullName == className)
                    {
                        classMatches.Add(cls);
                    }
                }
            }

            if (classMatches.Count == 0)
            {
                binder.Diagnostics.ReportEntryClassNotFound(location, className);
                return null;
            }

            if (classMatches.Count > 1)
            {
                binder.Diagnostics.ReportEntryClassAmbiguous(location, className);
                return null;
            }

            var classType = classMatches[0];
            var method = classType.IsInterface ? null : classType.GetDeclaredMethod(methodName);
            if (method == null || !method.IsStatic)
            {
                binder.Diagnostics.ReportEntryMethodNotFound(location, className, methodName);
                return null;
            }

            return method;
        }

        private static bool TryGetIntConstant(BoundExpression expression, out int value)
        {
            if (expression.ConstantValue?.Value is int intValue)
            {
                value = intValue;
                return true;
            }

            if (expression is BoundUnaryExpression unary &&
                unary.Op.Kind == BoundUnaryOperatorKind.Negation &&
                unary.Operand.ConstantValue?.Value is int operandValue)
            {
                value = -operandValue;
                return true;
            }

            value = 0;
            return false;
        }

        private static bool IsNumeric(TypeSymbol type)
        {
            return type.IsNumeric && !type.IsPlaceholder128;
        }

        /// <summary>6e-M19 M5-a：可空引用型（类/接口/string/数组/any）——null 字面量的合法转换目标。</summary>
        private static bool IsNullableReferenceType(TypeSymbol type)
        {
            return type is ClassTypeSymbol || type == TypeSymbol.String ||
                   type == TypeSymbol.Any || type.ElementType != null;
        }

        /// <summary>6e-M21 Phase 4/6：可接受范围内常量隐式窄化的目标整型（含 64 位：ulong y = 2 与 C# 同构）。</summary>
        private static bool IsNarrowIntegerTarget(TypeSymbol type)
        {
            return type == TypeSymbol.Int8 || type == TypeSymbol.Int16 ||
                   type == TypeSymbol.UInt8 || type == TypeSymbol.UInt16 ||
                   type == TypeSymbol.UInt32 || type == TypeSymbol.Int64 ||
                   type == TypeSymbol.UInt64;
        }

        private static bool FitsInIntegerType(long value, TypeSymbol type)
        {
            if (type.IsSigned)
            {
                return type.BitWidth switch
                {
                    8 => value >= sbyte.MinValue && value <= sbyte.MaxValue,
                    16 => value >= short.MinValue && value <= short.MaxValue,
                    32 => value >= int.MinValue && value <= int.MaxValue,
                    _ => true,
                };
            }

            return type.BitWidth switch
            {
                8 => value >= 0 && value <= byte.MaxValue,
                16 => value >= 0 && value <= ushort.MaxValue,
                32 => value >= 0 && value <= uint.MaxValue,
                _ => value >= 0,
            };
        }

        /// <summary>取整数常量（含一元负号），任意整数装箱表示均可。</summary>
        private static bool TryGetIntegerConstant(BoundExpression expression, out long value)
        {
            var constant = expression.ConstantValue?.Value;
            if (constant is int or long or sbyte or short or byte or ushort or uint or char)
            {
                value = NumericBox.ToSigned64(constant);
                return true;
            }

            if (expression is BoundUnaryExpression unary &&
                unary.Op.Kind == BoundUnaryOperatorKind.Negation &&
                unary.Operand.ConstantValue != null)
            {
                value = unchecked(-NumericBox.ToSigned64(unary.Operand.ConstantValue.Value));
                return true;
            }

            value = 0;
            return false;
        }

        private void BindEnumDeclaration(EnumDeclarationSyntax syntax, string @namespace = "")
        {
            var members = new Dictionary<string, int>();
            var nextValue = 0;

            foreach (var member in syntax.Members)
            {
                var memberName = member.Identifier.Text;

                if (members.ContainsKey(memberName))
                {
                    _diagnostics.ReportSymbolAlreadyDeclared(member.Identifier.Location, memberName);
                }
                else if (member.Value != null)
                {
                    var boundValue = BindExpression(member.Value);
                    if (TryGetIntConstant(boundValue, out var intValue))
                    {
                        nextValue = intValue;
                        members.Add(memberName, nextValue);
                    }
                    else
                    {
                        _diagnostics.ReportEnumMemberValueMustBeInt(member.Value.Location, memberName);
                    }
                }
                else
                {
                    members.Add(memberName, nextValue);
                }

                nextValue = nextValue + 1;
            }

            var enumType = new EnumTypeSymbol(syntax.Identifier.Text, members, @namespace);

            if (!_scope.TryDeclareEnum(enumType))
            {
                _diagnostics.ReportSymbolAlreadyDeclared(syntax.Identifier.Location, syntax.Identifier.Text);
            }
        }
    }
}
