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

        private readonly List<string> _usingNamespaces = new List<string>();
        private readonly List<string> _usingStatics = new List<string>();
        private readonly Dictionary<string, string> _usingAliases = new Dictionary<string, string>();

        private Stack<(BoundLabel BreakLabel, BoundLabel ContinueLabel)> _loopStack = new Stack<(BoundLabel BreakLabel, BoundLabel ContinueLabel)>();
        private int _labelCounter;
        private BoundScope _scope;

        private Binder(bool isScript, BoundScope? parent, FunctionSymbol? function, ImmutableArray<string> references, ImmutableArray<string> usingNamespaces, ImmutableArray<string> usingStatics = default, ImmutableDictionary<string, string> usingAliases = null)
        {
            _scope = new BoundScope(parent);
            _isScript = isScript;
            _function = function;
            _currentClass = function?.ContainingClass;
            _references = references.ToArray();
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
            var parentScope = CreateParentScope(previous);
            InjectCodSymbols(parentScope, codLibraries);
            var binder = new Binder(isScript, parentScope, null, references?.ToImmutableArray() ?? ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

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

            // 阶段 3：绑定接口（基接口 + 抽象成员）→ 类成员 → 接口实现完整性检查
            foreach (var (syntax, ns, symbol) in interfaceSymbols)
            {
                binder.BindInterfaceDeclaration(syntax, symbol, classFunctions);
            }

            // 阶段 3.5：绑定类成员（字段/方法/构造/基类）——部分类每个部分分别绑定，隐式默认构造在所有部分之后统一生成
            foreach (var (classType, parts) in classGroups)
            {
                var primary = parts[0].Syntax;

                for (var i = 0; i < parts.Count; i++)
                {
                    var (syntax, ns) = parts[i];
                    binder.BindClassBase(syntax, classType);
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

        public static BoundProgram BindProgram(bool isScript, BoundProgram? previous, BoundGlobalScope globalScope, ImmutableArray<CodProgram> codLibraries = default)
        {
            var parentScope = CreateParentScope(globalScope);
            InjectCodSymbols(parentScope, codLibraries);

            if (globalScope.Diagnostics.HasErrors())
            {
                return new BoundProgram(previous, globalScope.Diagnostics, null, null, ImmutableDictionary<FunctionSymbol, BoundBlockStatement>.Empty, globalScope.Classes);
            }

            var functionBodies = ImmutableDictionary.CreateBuilder<FunctionSymbol, BoundBlockStatement>();
            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

            foreach (var function in globalScope.Functions)
            {
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

                var bodySyntax = function.Declaration?.Body;
                var bodyLocation = (SyntaxNode?)function.Declaration?.Identifier ?? function.Syntax;

                if (function.Syntax is ConstructorDeclarationSyntax ctorSyntax)
                {
                    bodySyntax = ctorSyntax.Body;
                    bodyLocation = (SyntaxNode?)ctorSyntax.ConstructorKeyword ?? ctorSyntax.OpenParenthesisToken;
                }

                var binder = new Binder(isScript, parentScope, function, globalScope.References, globalScope.UsingNamespaces, globalScope.UsingStatics, globalScope.UsingAliases);
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
                ImmutableArray<BoundStatement> prefixStatements = ImmutableArray<BoundStatement>.Empty;
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

                functionBodies.Add(function, loweredBody);
                diagnostics.AddRange(binder.Diagnostics);
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

            // 合并 `.cod` 库函数体（语义层 BoundProgram 合并）；消费方同名函数优先
            if (!codLibraries.IsDefaultOrEmpty)
            {
                foreach (var library in codLibraries)
                {
                    foreach (var (fn, body) in library.Bodies)
                    {
                        if (!functionBodies.ContainsKey(fn))
                        {
                            functionBodies.Add(fn, body);
                        }
                    }
                }
            }

            return new BoundProgram(previous, diagnostics.ToImmutable(), globalScope.MainFunction, globalScope.ScriptFunction, functionBodies.ToImmutable(), globalScope.Classes);
        }

        private void BindFunctionDeclaration(FunctionDeclarationSyntax syntax, string? namespaceName = null, string? importedDll = null)
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
                    var parameter = new ParameterSymbol(parameterName, parameterType, parameters.Count);
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

            var function = new FunctionSymbol(syntax.Identifier.Text, parameters.ToImmutable(), type, syntax, isExtern, importedDll, callingConvention, @namespace: namespaceName ?? "");
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
                    var parameter = new ParameterSymbol(parameterName, parameterType, parameters.Count);
                    parameters.Add(parameter);
                }
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

            var classType = new ClassTypeSymbol(name, primary.Namespace, visibility, primary.Syntax);
            classType.IsAbstract = parts.Any(p => p.Syntax.Modifiers.Any(m => m.Kind == SyntaxKind.AbstractKeyword));
            classType.IsSealed = parts.Any(p => p.Syntax.Modifiers.Any(m => m.Kind == SyntaxKind.SealedKeyword));

            if (!_scope.TryDeclareClass(classType))
            {
                _diagnostics.ReportSymbolAlreadyDeclared(primary.Syntax.Identifier.Location, name);
            }

            return classType;
        }

        private void BindClassBase(ClassDeclarationSyntax syntax, ClassTypeSymbol classType)
        {
            // 基类型解析（`class Foo: Bar, IA, IB`；首个非接口 = 基类，其余须为接口；部分类多段声明时基类必须一致）
            var seenNonInterface = false;
            foreach (var baseClause in syntax.BaseTypes)
            {
                var baseName = baseClause.Identifier.Text;
                var baseType = LookupType(baseName) as ClassTypeSymbol;

                if (baseType == null)
                {
                    _diagnostics.ReportUndefinedType(baseClause.Location, baseName);
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

        private void BindClassMembers(ClassDeclarationSyntax syntax, ClassTypeSymbol classType, List<FunctionSymbol> classFunctions, string @namespace)
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
            }
        }

        /// <summary>隐式默认构造：类所有部分均未声明构造时生成无参构造。</summary>
        private void DeclareImplicitConstructor(ClassTypeSymbol classType, List<FunctionSymbol> classFunctions, ClassDeclarationSyntax syntax)
        {
            if (classType.GetDeclaredMethod(classType.Name) == null)
            {
                var ctor = new FunctionSymbol(classType.Name, ImmutableArray<ParameterSymbol>.Empty, TypeSymbol.Void, null, syntax: syntax, containingClass: classType, visibility: Visibility.Public) { IsConstructor = true };
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

            if (!_scope.TryDeclareClass(classType))
            {
                _diagnostics.ReportSymbolAlreadyDeclared(syntax.Identifier.Location, name);
            }

            return classType;
        }

        /// <summary>绑定接口声明：基接口列表 + 抽象成员（函数签名/属性访问器）。</summary>
        private void BindInterfaceDeclaration(InterfaceDeclarationSyntax syntax, ClassTypeSymbol interfaceType, List<FunctionSymbol> classFunctions)
        {
            // 基接口（仅允许接口）
            foreach (var baseClause in syntax.BaseTypes)
            {
                var baseName = baseClause.Identifier.Text;
                var baseType = LookupType(baseName) as ClassTypeSymbol;

                if (baseType == null)
                {
                    _diagnostics.ReportUndefinedType(baseClause.Location, baseName);
                }
                else if (!baseType.IsInterface)
                {
                    _diagnostics.ReportError(baseClause.Location, $"接口 '{interfaceType.Name}' 只能继承接口，不能继承类 '{baseName}'。");
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
                    var parameters = BindParameters(methodDeclaration.Parameters);
                    var returnType = BindTypeClause(methodDeclaration.Type) ?? TypeSymbol.Void;

                    if (interfaceType.GetDeclaredMethod(methodDeclaration.Identifier.Text) == null)
                    {
                        var method = new FunctionSymbol(methodDeclaration.Identifier.Text, parameters, returnType, methodDeclaration, containingClass: interfaceType, visibility: visibility)
                        {
                            IsAbstract = true,
                            IsVirtual = true,
                        };
                        interfaceType.AddMethod(method);
                        classFunctions.Add(method);
                    }
                    else
                    {
                        _diagnostics.ReportSymbolAlreadyDeclared(methodDeclaration.Identifier.Location, methodDeclaration.Identifier.Text);
                    }
                }
                else if (member is PropertyDeclarationSyntax propertyDeclaration)
                {
                    BindInterfacePropertyDeclaration(propertyDeclaration, interfaceType, classFunctions);
                }
            }
        }

        /// <summary>接口属性：getter/setter 访问器（无实现、抽象）。</summary>
        private void BindInterfacePropertyDeclaration(PropertyDeclarationSyntax syntax, ClassTypeSymbol interfaceType, List<FunctionSymbol> classFunctions)
        {
            var propertyType = BindTypeClause(syntax.Type);
            var visibility = GetVisibility(syntax.Modifiers, Visibility.Public);

            // 访问器可见性：独立计算 + 严格 C# 校验（CS0273 / 至多一个访问器带修饰符）
            ValidateAccessorVisibility(syntax, visibility);
            var getterVisibility = syntax.Getter != null ? GetVisibility(syntax.Getter.Modifiers, visibility) : visibility;
            var setterVisibility = syntax.Setter != null ? GetVisibility(syntax.Setter.Modifiers, visibility) : visibility;

            if (interfaceType.GetProperty(syntax.Identifier.Text) != null)
            {
                _diagnostics.ReportSymbolAlreadyDeclared(syntax.Identifier.Location, syntax.Identifier.Text);
                return;
            }

            FunctionSymbol? getter = null;
            if (syntax.Getter != null)
            {
                getter = new FunctionSymbol("get_" + syntax.Identifier.Text, ImmutableArray<ParameterSymbol>.Empty, propertyType, null,
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
                var valueParameter = new ParameterSymbol("value", propertyType, 0);
                setter = new FunctionSymbol("set_" + syntax.Identifier.Text, ImmutableArray.Create(valueParameter), TypeSymbol.Void, null,
                    syntax: syntax.Setter, containingClass: interfaceType, visibility: setterVisibility)
                {
                    IsAbstract = true,
                    IsVirtual = true,
                };
                interfaceType.AddMethod(setter);
                classFunctions.Add(setter);
            }

            interfaceType.AddProperty(new PropertySymbol(syntax.Identifier.Text, propertyType, interfaceType, getter, setter, visibility, isStatic: false));
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
                var method = current.GetDeclaredMethod(interfaceMethod.Name);
                if (method == null)
                {
                    continue;
                }

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
                    if (method.Parameters[i].Type != interfaceMethod.Parameters[i].Type)
                    {
                        parametersMatch = false;
                        break;
                    }
                }

                if (!parametersMatch || method.ReturnType != interfaceMethod.ReturnType)
                {
                    continue;
                }

                return method;
            }

            return null;
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

        private void BindPropertyDeclaration(PropertyDeclarationSyntax syntax, ClassTypeSymbol classType, List<FunctionSymbol> classFunctions)        {
            var propertyType = BindTypeClause(syntax.Type);
            var visibility = GetVisibility(syntax.Modifiers, Visibility.Private);
            var isStatic = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword);
            var isAuto = syntax.IsAuto;

            // 访问器可见性：独立计算 + 严格 C# 校验（CS0273 / 至多一个访问器带修饰符）
            ValidateAccessorVisibility(syntax, visibility);
            var getterVisibility = syntax.Getter != null ? GetVisibility(syntax.Getter.Modifiers, visibility) : visibility;
            var setterVisibility = syntax.Setter != null ? GetVisibility(syntax.Setter.Modifiers, visibility) : visibility;

            // 后备字段（自动属性）
            FieldSymbol? backingField = null;
            if (isAuto)
            {
                var backingName = "_" + syntax.Identifier.Text;
                if (classType.GetDeclaredField(backingName) == null)
                {
                    backingField = new FieldSymbol(backingName, propertyType, Visibility.Private, classType);
                    classType.AddField(backingField);
                }
            }

            // getter：get_Name
            FunctionSymbol? getter = null;
            if (syntax.Getter != null)
            {
                getter = new FunctionSymbol("get_" + syntax.Identifier.Text, ImmutableArray<ParameterSymbol>.Empty, propertyType, null,
                    syntax: syntax.Getter, containingClass: classType, visibility: getterVisibility) { IsStatic = isStatic };
                classType.AddMethod(getter);
                classFunctions.Add(getter);
            }

            // setter：set_Name（value 隐式参数）
            FunctionSymbol? setter = null;
            if (syntax.Setter != null)
            {
                var valueParameter = new ParameterSymbol("value", propertyType, 0);
                setter = new FunctionSymbol("set_" + syntax.Identifier.Text, ImmutableArray.Create(valueParameter), TypeSymbol.Void, null,
                    syntax: syntax.Setter, containingClass: classType, visibility: setterVisibility) { IsStatic = isStatic };
                classType.AddMethod(setter);
                classFunctions.Add(setter);
            }

            if (classType.GetDeclaredProperty(syntax.Identifier.Text) == null)
            {
                classType.AddProperty(new PropertySymbol(syntax.Identifier.Text, propertyType, classType, getter, setter, visibility, isStatic));
            }
            else
            {
                _diagnostics.ReportSymbolAlreadyDeclared(syntax.Identifier.Location, syntax.Identifier.Text);
            }
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

        private FunctionSymbol BindClassMethodDeclaration(FunctionDeclarationSyntax syntax, ClassTypeSymbol classType, string? dllName = null)
        {
            var parameters = BindParameters(syntax.Parameters);
            var type = BindTypeClause(syntax.Type) ?? TypeSymbol.Void;
            var isSyscall = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.SyscallKeyword);
            var isExtern = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.CdeclKeyword || m.Kind == SyntaxKind.StdcallKeyword);
            // syscall/extern 方法缺省 public（System.Runtime.Runtime.Print 供 System.Console 封装层调用；extern 供类外限定调用）
            var visibility = GetVisibility(syntax.Modifiers, (isSyscall || isExtern) ? Visibility.Public : Visibility.Private);
            var isStatic = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword);
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

            // syscall 方法隐含 static（System.Runtime.Runtime.Print 类名调用）
            var method = new FunctionSymbol(syntax.Identifier.Text, parameters, type, syntax, isExtern: isExtern, dllName: dllName, callingConvention: GetCallingConvention(syntax), containingClass: classType, visibility: visibility, builtinKind: builtinKind)
            {
                IsStatic = isStatic || isSyscall,
                IsVirtual = isVirtual,
                IsOverride = isOverride,
                IsAbstract = isAbstract,
                IsSealed = isSealed,
            };

            // override 语义：绑定到基类同签名 virtual/abstract 方法
            if (isOverride)
            {
                if (classType.BaseType == null)
                {
                    _diagnostics.ReportError(syntax.Identifier.Location, $"方法 '{syntax.Identifier.Text}' 标记 override，但类型没有基类。");
                }
                else
                {
                    var baseMethod = classType.BaseType.GetMethod(syntax.Identifier.Text);
                    if (baseMethod == null || !baseMethod.IsVirtual && !baseMethod.IsAbstract || baseMethod.IsSealed)
                    {
                        _diagnostics.ReportError(syntax.Identifier.Location, $"基类中找不到可重写的 virtual/abstract 方法 '{syntax.Identifier.Text}'。");
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
            foreach (var blockMember in importBlock.Members)
            {
                if (blockMember is FunctionDeclarationSyntax functionDeclaration)
                {
                    // 块内只允许 extern 函数声明（stdcall/cdecl）；普通带体函数 → 诊断
                    var isExternDecl = functionDeclaration.Modifiers.Any(m => m.Kind == SyntaxKind.CdeclKeyword || m.Kind == SyntaxKind.StdcallKeyword);
                    if (!isExternDecl)
                    {
                        _diagnostics.ReportImportBlockOnlyExternFunctions(functionDeclaration.Identifier.Location);
                    }

                    var method = BindClassMethodDeclaration(functionDeclaration, classType, dllName: importBlock.DllName);

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

        private BoundConstructorChainExpression? BindConstructorChain(ConstructorDeclarationSyntax syntax, ClassTypeSymbol classType)
        {
            var isBase = syntax.InitializerKeyword!.Kind == SyntaxKind.BaseKeyword;
            var targetClass = isBase ? classType.BaseType : classType;

            if (targetClass == null)
            {
                _diagnostics.ReportError(syntax.InitializerKeyword!.Location, "类型没有基类，不能调用 base(...)。");
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

            if (type == TypeSymbol.Int32 || type == TypeSymbol.Byte)
            {
                return 0;
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

            if (_currentClass.BaseType == null)
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

            if (_function?.IsStatic == true)
            {
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

            var type = LookupType(syntax.Identifier.Text);
            if (type == null)
            {
                _diagnostics.ReportUndefinedType(syntax.Identifier.Location, syntax.Identifier.Text);
            }

            return type!;
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
                elementType = TypeSymbol.Error;
                _diagnostics.ReportError(syntax.Collection.Location, $"foreach 只能遍历数组或字符串，不能遍历 '{collection.Type}'。");
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
                if (_function.ReturnType == TypeSymbol.Void)
                {
                    if (expression != null)
                        _diagnostics.ReportInvalidReturnExpression(syntax.Expression!.Location, _function.Name);
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

        private BoundStatement BindExpressionStatement(ExpressionStatementSyntax syntax)
        {
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
                default:
                    throw new Exception($"Unexpected syntax {syntax.Kind}");
            }
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
                    return new BoundMemberCallExpression(syntax, propertyGetCall.Expression, property.Setter.Name, ImmutableArray.Create(converted), TypeSymbol.Void, property.Setter, propertyGetCall.IsBase);
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

            if (boundTarget is BoundMemberAccessExpression memberTarget && memberTarget.Field != null && syntax.AssignmentToken.Kind == SyntaxKind.EqualsToken)
            {
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
            var classType = LookupType(syntax.Identifier.Text) as ClassTypeSymbol;
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

            if (boundIndex.Type != TypeSymbol.Error && boundIndex.Type != TypeSymbol.Int32)
            {
                _diagnostics.ReportCannotConvert(syntax.Index.Location, boundIndex.Type, TypeSymbol.Int32);
                boundIndex = new BoundErrorExpression(syntax.Index);
            }

            if (boundTarget.Type == TypeSymbol.Error)
            {
                return new BoundErrorExpression(syntax);
            }

            if (boundTarget.Type == TypeSymbol.String)
            {
                return new BoundElementAccessExpression(syntax, TypeSymbol.Char, boundTarget, boundIndex);
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

            _diagnostics.ReportUnknownMember(syntax.IdentifierToken.Location, identifier, boundExpression.Type);
            return new BoundErrorExpression(syntax);
        }

        /// <summary>命名空间限定函数调用解析：`System.Math.Max(...)`（精确前缀）或 `using System;` + `Math.Max(...)`（using 前缀）。</summary>
        private bool TryBindNamespaceFunctionCall(MemberCallExpressionSyntax syntax, string identifier, out BoundExpression result)
        {
            result = null!;

            if (!(ResolveDottedTypeName(syntax.Expression) is string prefix) || prefix.Length == 0)
            {
                return false;
            }

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
                boundArguments[i] = BindConversion(syntax.Arguments[i].Location, boundArguments[i], function.Parameters[i].Type);
            }

            result = new BoundCallExpression(syntax, function, boundArguments.ToImmutable());
            return true;
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
            var boundOperator = BoundBinaryOperator.Bind(syntax.OperatorToken.Kind, boundLeft.Type, boundRight.Type);

            if (boundOperator == null && boundLeft.Type != TypeSymbol.Error && boundRight.Type != TypeSymbol.Error &&
                IsNumeric(boundLeft.Type) && IsNumeric(boundRight.Type))
            {
                if (Conversion.Classify(boundLeft.Type, boundRight.Type).IsImplicit)
                {
                    boundLeft = BindConversion(boundLeft.Syntax.Location, boundLeft, boundRight.Type, allowExplicit: false);
                    boundOperator = BoundBinaryOperator.Bind(syntax.OperatorToken.Kind, boundLeft.Type, boundRight.Type);
                }
                else if (Conversion.Classify(boundRight.Type, boundLeft.Type).IsImplicit)
                {
                    boundRight = BindConversion(boundRight.Syntax.Location, boundRight, boundLeft.Type, allowExplicit: false);
                    boundOperator = BoundBinaryOperator.Bind(syntax.OperatorToken.Kind, boundLeft.Type, boundRight.Type);
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
            if (syntax.Arguments.Count == 1 && LookupType(syntax.Identifier.Text) is TypeSymbol type)
            {
                return BindConversion(syntax.Arguments[0], type, allowExplicit: true);
            }

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

                boundArguments[i] = BindConversion(argumentLocation, argument, parameter.Type);
            }

            return new BoundCallExpression(syntax, function, boundArguments.ToImmutable());
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
                boundArguments[i] = BindConversion(syntax.Arguments[i].Location, boundArguments[i], method.Parameters[i].Type);
            }

            return new BoundMemberCallExpression(syntax, target, method.Name, boundArguments.ToImmutable(), method.ReturnType, method);
        }

        private BoundExpression BindConversion(ExpressionSyntax syntax, TypeSymbol type, bool allowExplicit = false)
        {
            var expression = BindExpression(syntax);

            return BindConversion(syntax.Location, expression, type, allowExplicit);
        }

        private BoundExpression BindConversion(TextLocation diagnosticLocation, BoundExpression expression, TypeSymbol type, bool allowExplicit = false)
        {
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
                if (type == TypeSymbol.Byte && TryGetIntConstant(expression, out var intValue))
                {
                    if (intValue < 0 || intValue > 255)
                    {
                        _diagnostics.ReportByteConstantOutOfRange(diagnosticLocation, intValue);
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
            switch (name)
            {
                case "any": return TypeSymbol.Any;
                case "bool": return TypeSymbol.Boolean;
                case "int": return TypeSymbol.Int32;
                case "byte": return TypeSymbol.Byte;
                case "double": return TypeSymbol.Double;
                case "char": return TypeSymbol.Char;
                case "string": return TypeSymbol.String;
                case "void": return TypeSymbol.Void;
                default:
                {
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

                        var externalType = ExternalTypeResolver.TryResolve(fullName, _references);
                        if (externalType != null)
                        {
                            return externalType;
                        }
                    }

                    return null;
                }
            }
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
            return type == TypeSymbol.Int32 || type == TypeSymbol.Byte || type == TypeSymbol.Double;
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
