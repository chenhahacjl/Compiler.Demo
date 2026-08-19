using Cocoa.CodeAnalysis.Lowering;
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

        private Stack<(BoundLabel BreakLabel, BoundLabel ContinueLabel)> _loopStack = new Stack<(BoundLabel BreakLabel, BoundLabel ContinueLabel)>();
        private int _labelCounter;
        private BoundScope _scope;

        private Binder(bool isScript, BoundScope? parent, FunctionSymbol? function, ImmutableArray<string> references, ImmutableArray<string> usingNamespaces)
        {
            _scope = new BoundScope(parent);
            _isScript = isScript;
            _function = function;
            _currentClass = function?.ContainingClass;
            _references = references.ToArray();
            _usingNamespaces.AddRange(usingNamespaces);

            if (function != null)
            {
                foreach (var parameter in function.Parameters)
                {
                    _scope.TryDeclareVariable(parameter);
                }
            }
        }

        public static BoundGlobalScope BindGlobalScope(bool isScript, BoundGlobalScope? previous, ImmutableArray<SyntaxTree> syntaxTrees, string entryPointName = "Main", string[]? references = null)
        {
            var parentScope = CreateParentScope(previous);
            var binder = new Binder(isScript, parentScope, null, references?.ToImmutableArray() ?? ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

            binder.Diagnostics.AddRange(syntaxTrees.SelectMany(st => st.Diagnostics));
            if (binder.Diagnostics.Any())
            {
                return new BoundGlobalScope(previous, binder.Diagnostics.ToImmutableArray(), null, null, ImmutableArray<FunctionSymbol>.Empty, ImmutableArray<EnumTypeSymbol>.Empty, ImmutableArray<ClassTypeSymbol>.Empty, ImmutableArray<VariableSymbol>.Empty, ImmutableArray<BoundStatement>.Empty, ImmutableArray<string>.Empty, (references ?? Array.Empty<string>()).ToImmutableArray());
            }

            var globalStatements = syntaxTrees.SelectMany(st => st.Root.Members)
                                              .OfType<GlobalStatementSyntax>();

            string? importedDll = null;

            var classFunctions = new List<FunctionSymbol>();
            var allClasses = new List<(ClassDeclarationSyntax Syntax, string Namespace)>();

            // 阶段 1：处理 import/function/enum/using + 收集所有类声明（递归 namespace）
            foreach (var member in syntaxTrees.SelectMany(st => st.Root.Members))
            {
                if (member is ImportClauseSyntax importClause)
                {
                    importedDll = importClause.DllName;
                }
                else if (member is FunctionDeclarationSyntax function)
                {
                    binder.BindFunctionDeclaration(function, importedDll);
                }
                else if (member is EnumDeclarationSyntax enumDeclaration)
                {
                    binder.BindEnumDeclaration(enumDeclaration);
                }
                else if (member is ClassDeclarationSyntax classDeclaration)
                {
                    allClasses.Add((classDeclaration, ""));
                }
                else if (member is NamespaceDeclarationSyntax namespaceDeclaration)
                {
                    binder.CollectClasses(namespaceDeclaration, "", allClasses);
                    binder.BindNamespaceFunctions(namespaceDeclaration, importedDll);
                }
                else if (member is UsingDirectiveSyntax usingDirective)
                {
                    binder._usingNamespaces.Add(usingDirective.Name);
                }
            }

            // 阶段 2：声明所有类壳（两阶段：类可前向引用基类）
            foreach (var (syntax, ns) in allClasses)
            {
                binder.DeclareClassDeclaration(syntax, ns);
            }

            // 阶段 3：绑定类成员（字段/方法/构造/基类）
            foreach (var (syntax, ns) in allClasses)
            {
                var classType = binder._scope.TryLookupSymbol(syntax.Identifier.Text) as ClassTypeSymbol;
                if (classType != null)
                {
                    binder.BindClassMembers(syntax, classType, classFunctions, ns);
                }
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

                mainFunction = functions.SingleOrDefault(f => f.Name == entryPointName);

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

            if (previous != null)
            {
                diagnostics = diagnostics.InsertRange(0, previous.Diagnostics);
            }

            return new BoundGlobalScope(previous, diagnostics, mainFunction, scriptFunction, functions, enums, classes, variables, statements.ToImmutable(), usingNamespaces, (references ?? Array.Empty<string>()).ToImmutableArray());
        }

        public static BoundProgram BindProgram(bool isScript, BoundProgram? previous, BoundGlobalScope globalScope)
        {
            var parentScope = CreateParentScope(globalScope);

            if (globalScope.Diagnostics.Any())
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

                var bodySyntax = function.Declaration?.Body;
                var bodyLocation = (SyntaxNode?)function.Declaration?.Identifier ?? function.Syntax;

                if (function.Syntax is ConstructorDeclarationSyntax ctorSyntax)
                {
                    bodySyntax = ctorSyntax.Body;
                    bodyLocation = ctorSyntax.ConstructorKeyword;
                }

                var binder = new Binder(isScript, parentScope, function, globalScope.References, globalScope.UsingNamespaces);
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
                if (function.Syntax is ConstructorDeclarationSyntax chainCtor && chainCtor.InitializerKeyword != null)
                {
                    var chain = binder.BindConstructorChain(chainCtor, function.ContainingClass!);
                    if (chain != null)
                    {
                        body = new BoundBlockStatement(bodySyntax ?? function.Syntax!, new[] { new BoundExpressionStatement(chainCtor, chain) }.Concat(body.Statements).ToImmutableArray());
                    }
                }
                // 隐式默认构造：基类非 Object 时插入 base() 链调用
                else if (function.Syntax is ClassDeclarationSyntax implicitCtor && function.ContainingClass != null &&
                         function.ContainingClass.BaseType != null)
                {
                    var baseCtor = function.ContainingClass.BaseType.GetMethod(function.ContainingClass.BaseType.Name);
                    if (baseCtor != null)
                    {
                        var chain = new BoundConstructorChainExpression(implicitCtor, ConstructorInitializerKind.Base, baseCtor, ImmutableArray<BoundExpression>.Empty);
                        body = new BoundBlockStatement(bodySyntax ?? function.Syntax!, new[] { new BoundExpressionStatement(implicitCtor, chain) }.Concat(body.Statements).ToImmutableArray());
                    }
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

            return new BoundProgram(previous, diagnostics.ToImmutable(), globalScope.MainFunction, globalScope.ScriptFunction, functionBodies.ToImmutable(), globalScope.Classes);
        }

        private void BindFunctionDeclaration(FunctionDeclarationSyntax syntax, string? importedDll = null)
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

            if (isExtern)
            {
                if (importedDll == null)
                {
                    _diagnostics.ReportExternFunctionWithoutImport(syntax.Identifier.Location);
                }

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

            var function = new FunctionSymbol(syntax.Identifier.Text, parameters.ToImmutable(), type, syntax, isExtern, importedDll, callingConvention);
            if (syntax.Identifier.Text != null && !_scope.TryDeclareFunction(function))
            {
                _diagnostics.ReportSymbolAlreadyDeclared(syntax.Identifier.Location, function.Name);
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

        private void DeclareClassDeclaration(ClassDeclarationSyntax syntax, string @namespace)
        {
            var name = syntax.Identifier.Text;
            var isPublic = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.PublicKeyword);

            var classType = new ClassTypeSymbol(name, @namespace, isPublic, syntax);
            classType.IsAbstract = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.AbstractKeyword);
            classType.IsSealed = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.SealedKeyword);

            if (!_scope.TryDeclareClass(classType))
            {
                _diagnostics.ReportSymbolAlreadyDeclared(syntax.Identifier.Location, name);
            }
        }

        private void BindClassMembers(ClassDeclarationSyntax syntax, ClassTypeSymbol classType, List<FunctionSymbol> classFunctions, string @namespace)
        {
            // 基类解析（`class Foo: Bar`）
            if (syntax.BaseType != null)
            {
                var baseName = syntax.BaseType.Identifier.Text;
                var baseType = _scope.TryLookupSymbol(baseName) as ClassTypeSymbol;

                if (baseType == null)
                {
                    _diagnostics.ReportUndefinedType(syntax.BaseType.Location, baseName);
                }
                else if (baseType.IsSealed)
                {
                    _diagnostics.ReportCannotInheritSealed(syntax.Identifier.Location, baseName);
                }
                else if (baseType.IsBaseOf(classType))
                {
                    _diagnostics.ReportCircularInheritance(syntax.Identifier.Location, baseName);
                }
                else
                {
                    classType.BaseType = baseType;
                }
            }

            foreach (var member in syntax.Members)
            {
                if (classType.IsStatic &&
                    (member is ClassFieldDeclarationSyntax && !member.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword) ||
                     member is FunctionDeclarationSyntax && !member.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword)))
                {
                    _diagnostics.ReportError(member.Location, $"静态类 {classType.Name} 只能包含静态成员。");
                }

                if (member is ClassFieldDeclarationSyntax fieldDeclaration)
                {
                    var fieldType = BindTypeClause(fieldDeclaration.Type);
                    var fieldIsPublic = fieldDeclaration.Modifiers.Any(m => m.Kind == SyntaxKind.PublicKeyword);
                    var fieldIsReadonly = fieldDeclaration.Modifiers.Any(m => m.Kind == SyntaxKind.ReadonlyKeyword);
                    var fieldIsStatic = fieldDeclaration.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword);

                    if (classType.GetDeclaredField(fieldDeclaration.Identifier.Text) == null)
                    {
                        classType.AddField(new FieldSymbol(fieldDeclaration.Identifier.Text, fieldType, fieldIsPublic, classType, isReadonly: fieldIsReadonly, isStatic: fieldIsStatic));
                    }
                    else
                    {
                        _diagnostics.ReportSymbolAlreadyDeclared(fieldDeclaration.Identifier.Location, fieldDeclaration.Identifier.Text);
                    }
                }
                else if (member is ConstructorDeclarationSyntax constructorDeclaration)
                {
                    var parameters = BindParameters(constructorDeclaration.Parameters);
                    var isPublicCtor = constructorDeclaration.Modifiers.Any(m => m.Kind == SyntaxKind.PublicKeyword);

                    if (classType.GetDeclaredMethod(classType.Name) == null)
                    {
                        var ctor = new FunctionSymbol(classType.Name, parameters, TypeSymbol.Void, null, syntax: constructorDeclaration, containingClass: classType, isPublic: isPublicCtor) { IsConstructor = true };
                        classType.AddMethod(ctor);
                        classFunctions.Add(ctor);
                    }
                    else
                    {
                        _diagnostics.ReportSymbolAlreadyDeclared(constructorDeclaration.ConstructorKeyword.Location, classType.Name);
                    }
                }
                else if (member is FunctionDeclarationSyntax methodDeclaration)
                {
                    var method = BindClassMethodDeclaration(methodDeclaration, classType);

                    if (classType.GetDeclaredMethod(methodDeclaration.Identifier.Text) == null)
                    {
                        classType.AddMethod(method);
                        classFunctions.Add(method);
                    }
                    else
                    {
                        _diagnostics.ReportSymbolAlreadyDeclared(methodDeclaration.Identifier.Location, methodDeclaration.Identifier.Text);
                    }
                }
                else if (member is PropertyDeclarationSyntax propertyDeclaration)
                {
                    BindPropertyDeclaration(propertyDeclaration, classType, classFunctions);
                }
            }

            // 隐式默认构造：类未声明任何构造时生成无参构造
            if (classType.GetDeclaredMethod(classType.Name) == null)
            {
                var ctor = new FunctionSymbol(classType.Name, ImmutableArray<ParameterSymbol>.Empty, TypeSymbol.Void, null, syntax: syntax, containingClass: classType, isPublic: true) { IsConstructor = true };
                classType.AddMethod(ctor);
                classFunctions.Add(ctor);
            }
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
            var isPublic = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.PublicKeyword);
            var isStatic = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword);
            var isAuto = syntax.IsAuto;

            // 后备字段（自动属性）
            FieldSymbol? backingField = null;
            if (isAuto)
            {
                var backingName = "_" + syntax.Identifier.Text;
                if (classType.GetDeclaredField(backingName) == null)
                {
                    backingField = new FieldSymbol(backingName, propertyType, isPublic: false, classType);
                    classType.AddField(backingField);
                }
            }

            // getter：get_Name
            FunctionSymbol? getter = null;
            if (syntax.Getter != null)
            {
                getter = new FunctionSymbol("get_" + syntax.Identifier.Text, ImmutableArray<ParameterSymbol>.Empty, propertyType, null,
                    syntax: syntax.Getter, containingClass: classType, isPublic: isPublic) { IsStatic = isStatic };
                classType.AddMethod(getter);
                classFunctions.Add(getter);
            }

            // setter：set_Name（value 隐式参数）
            FunctionSymbol? setter = null;
            if (syntax.Setter != null)
            {
                var valueParameter = new ParameterSymbol("value", propertyType, 0);
                setter = new FunctionSymbol("set_" + syntax.Identifier.Text, ImmutableArray.Create(valueParameter), TypeSymbol.Void, null,
                    syntax: syntax.Setter, containingClass: classType, isPublic: isPublic) { IsStatic = isStatic };
                classType.AddMethod(setter);
                classFunctions.Add(setter);
            }

            if (classType.GetProperty(syntax.Identifier.Text) == null)
            {
                classType.AddProperty(new PropertySymbol(syntax.Identifier.Text, propertyType, classType, getter, setter, isPublic, isStatic));
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

        private void BindNamespaceFunctions(NamespaceDeclarationSyntax syntax, string? importedDll)
        {
            foreach (var member in syntax.Members)
            {
                if (member is FunctionDeclarationSyntax functionDeclaration)
                {
                    BindFunctionDeclaration(functionDeclaration, importedDll);
                }
                else if (member is NamespaceDeclarationSyntax nested)
                {
                    BindNamespaceFunctions(nested, importedDll);
                }
            }
        }

        private FunctionSymbol BindClassMethodDeclaration(FunctionDeclarationSyntax syntax, ClassTypeSymbol classType)
        {
            var parameters = BindParameters(syntax.Parameters);
            var type = BindTypeClause(syntax.Type) ?? TypeSymbol.Void;
            var isPublic = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.PublicKeyword);
            var isStatic = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.StaticKeyword);
            var isVirtual = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.VirtualKeyword);
            var isOverride = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.OverrideKeyword);
            var isAbstract = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.AbstractKeyword);
            var isSealed = syntax.Modifiers.Any(m => m.Kind == SyntaxKind.SealedKeyword);

            var method = new FunctionSymbol(syntax.Identifier.Text, parameters, type, syntax, isExtern: false, containingClass: classType, isPublic: isPublic)
            {
                IsStatic = isStatic,
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

            foreach (var function in BuiltinFunctions.GetAll())
            {
                result.TryDeclareFunction(function);
            }

            return result;
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
            var isReadOnly = syntax.Keyword.Kind == SyntaxKind.LetKeyword;
            var type = BindTypeClause(syntax.TypeClause);
            var initializer = BindExpression(syntax.Initializer);
            var variableType = type ?? initializer.Type;
            var variable = BindVariableDeclaration(syntax.Identifier, isReadOnly, variableType, initializer.ConstantValue);
            var convertedInitializer = BindConversion(syntax.Initializer.Location, initializer, variableType);

            return new BoundVariableDeclaration(syntax, variable, convertedInitializer);
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

            _scope = new BoundScope(_scope);

            var variable = BindVariableDeclaration(syntax.Identifier, isReadOnly: true, TypeSymbol.Int32);
            var body = BindLoopBody(syntax.Body, out var breakLabel, out var continueLabel);

            _scope = _scope.Parent!;

            return new BoundForStatement(syntax, variable, lowerBound, upperBound, body, breakLabel, continueLabel);
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

            var continueLabel = _loopStack.Peek().ContinueLabel;
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
                case SyntaxKind.BinaryExpression: return BindBinaryExpression((BinaryExpressionSyntax)syntax);
                case SyntaxKind.CallExpression: return BindCallExpression((CallExpressionSyntax)syntax);
                case SyntaxKind.ArrayCreationExpression: return BindArrayCreationExpression((ArrayCreationExpressionSyntax)syntax);
                case SyntaxKind.ObjectCreationExpression: return BindObjectCreationExpression((ObjectCreationExpressionSyntax)syntax);
                case SyntaxKind.ElementAccessExpression: return BindElementAccessExpression((ElementAccessExpressionSyntax)syntax);
                case SyntaxKind.MemberAccessExpression: return BindMemberAccessExpression((MemberAccessExpressionSyntax)syntax);
                case SyntaxKind.MemberCallExpression: return BindMemberCallExpression((MemberCallExpressionSyntax)syntax);
                case SyntaxKind.CastExpression: return BindCastExpression((CastExpressionSyntax)syntax);
                case SyntaxKind.ThisExpression: return BindThisExpression((ThisExpressionSyntax)syntax);
                case SyntaxKind.BaseExpression: return BindBaseExpression((BaseExpressionSyntax)syntax);
                default:
                    throw new Exception($"Unexpected syntax {syntax.Kind}");
            }
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
                    var thisExpression = new BoundThisExpression(syntax, _currentClass);
                    return new BoundMemberAccessExpression(syntax, field.Type, thisExpression, name, field);
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
                if (!memberTarget.Field.IsPublic && _currentClass != memberTarget.Field.ContainingClass)
                {
                    _diagnostics.ReportCannotAccessPrivateMember(syntax.AssignmentToken.Location, memberTarget.Field.Name);
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
            if (ctor != null && ctor.Parameters.Length != arguments.Count)
            {
                _diagnostics.ReportWrongArgumentCount(syntax.Identifier.Location, classType.Name, ctor.Parameters.Length, arguments.Count);
                return new BoundErrorExpression(syntax);
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

            // 静态字段访问：MathHelpers.Count
            if (syntax.Expression is NameExpressionSyntax staticNameExpr &&
                LookupType(staticNameExpr.IdentifierToken.Text) is ClassTypeSymbol staticType &&
                staticType.GetField(identifier) is FieldSymbol staticField &&
                staticField.IsStatic)
            {
                return new BoundMemberAccessExpression(syntax, staticField.Type, new BoundStaticTypeExpression(syntax.Expression, staticType), identifier, staticField);
            }

            // 枚举成员访问（Color.Red）：左侧为枚举类型名 → 折叠为常量字面量
            if (syntax.Expression is NameExpressionSyntax nameExpression)
            {
                if (LookupType(nameExpression.IdentifierToken.Text) is EnumTypeSymbol enumType)
                {
                    if (enumType.TryGetMember(syntax.IdentifierToken.Text, out var value))
                    {
                        return new BoundLiteralExpression(syntax, value, enumType);
                    }

                    _diagnostics.ReportEnumMemberNotDefined(syntax.IdentifierToken.Location, enumType.Name, syntax.IdentifierToken.Text);
                    return new BoundErrorExpression(syntax);
                }
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
                    if (!field.IsPublic && _currentClass != classType)
                    {
                        _diagnostics.ReportCannotAccessPrivateMember(syntax.IdentifierToken.Location, identifier);
                        return new BoundErrorExpression(syntax);
                    }

                    return new BoundMemberAccessExpression(syntax, field.Type, boundTarget, identifier, field);
                }

                // 属性读：obj.Name → get_Name()
                var property = classType.GetProperty(identifier);
                if (property != null && property.Getter != null)
                {
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

            // 静态方法调用：MathHelpers.Square(2)（target 是类型名）
            if (syntax.Expression is NameExpressionSyntax staticNameExpr &&
                LookupType(staticNameExpr.IdentifierToken.Text) is ClassTypeSymbol staticType &&
                staticType.GetMethod(identifier) is FunctionSymbol staticMethod &&
                staticMethod.IsStatic)
            {
                var staticArguments = ImmutableArray.CreateBuilder<BoundExpression>();
                foreach (var argument in syntax.Arguments)
                {
                    staticArguments.Add(BindExpression(argument));
                }

                if (staticMethod.Parameters.Length != syntax.Arguments.Count)
                {
                    _diagnostics.ReportWrongArgumentCount(syntax.IdentifierToken.Location, identifier, staticMethod.Parameters.Length, syntax.Arguments.Count);
                    return new BoundErrorExpression(syntax);
                }

                var arguments = ImmutableArray.CreateBuilder<BoundExpression>();
                for (var i = 0; i < staticArguments.Count; i++)
                {
                    arguments.Add(BindConversion(syntax.Arguments[i].Location, staticArguments[i], staticMethod.Parameters[i].Type));
                }

                return new BoundMemberCallExpression(syntax, new BoundStaticTypeExpression(syntax.Expression, staticType), identifier, arguments.ToImmutable(), staticMethod.ReturnType, staticMethod);
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
                    if (!method.IsPublic && _currentClass != classType)
                    {
                        _diagnostics.ReportCannotAccessPrivateMember(syntax.IdentifierToken.Location, identifier);
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

        private BoundExpression BindUnaryExpression(UnaryExpressionSyntax syntax)
        {
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

            var symbol = _scope.TryLookupSymbol(syntax.Identifier.Text);

            // 类方法内：裸方法调用解析为本类方法（this.Method()）
            if (symbol == null && _currentClass != null)
            {
                var method = _currentClass.GetMethod(syntax.Identifier.Text);
                if (method != null)
                {
                    return BindMemberCall(syntax, new BoundThisExpression(syntax, _currentClass), method);
                }
            }

            if (symbol == null)
            {
                _diagnostics.ReportUndefinedFunction(syntax.Identifier.Location, syntax.Identifier.Text);
                return new BoundErrorExpression(syntax);
            }

            var function = symbol as FunctionSymbol;
            if (function == null)
            {
                _diagnostics.ReportNotAFunction(syntax.Identifier.Location, syntax.Identifier.Text);
                return new BoundErrorExpression(syntax);
            }

            if (syntax.Arguments.Count != function.Parameters.Length)
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
                default:
                {
                    var lookup = _scope.TryLookupSymbol(name);
                    if (lookup is TypeSymbol declaredType)
                    {
                        return declaredType;
                    }

                    // 外部类型：using 前缀 + 名字 → 引用程序集
                    foreach (var ns in _usingNamespaces)
                    {
                        var fullName = ns.Length == 0 ? name : ns + "." + name;
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

        private void BindEnumDeclaration(EnumDeclarationSyntax syntax)
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

            var enumType = new EnumTypeSymbol(syntax.Identifier.Text, members);

            if (!_scope.TryDeclareEnum(enumType))
            {
                _diagnostics.ReportSymbolAlreadyDeclared(syntax.Identifier.Location, syntax.Identifier.Text);
            }
        }
    }
}
