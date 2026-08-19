using Cocoa.CodeAnalysis.Lowering;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

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

        private Stack<(BoundLabel BreakLabel, BoundLabel ContinueLabel)> _loopStack = new Stack<(BoundLabel BreakLabel, BoundLabel ContinueLabel)>();
        private int _labelCounter;
        private BoundScope _scope;

        private Binder(bool isScript, BoundScope? parent, FunctionSymbol? function)
        {
            _scope = new BoundScope(parent);
            _isScript = isScript;
            _function = function;

            if (function != null)
            {
                foreach (var parameter in function.Parameters)
                {
                    _scope.TryDeclareVariable(parameter);
                }
            }
        }

        public static BoundGlobalScope BindGlobalScope(bool isScript, BoundGlobalScope? previous, ImmutableArray<SyntaxTree> syntaxTrees, string entryPointName = "Main")
        {
            var parentScope = CreateParentScope(previous);
            var binder = new Binder(isScript, parentScope, null);

            binder.Diagnostics.AddRange(syntaxTrees.SelectMany(st => st.Diagnostics));
            if (binder.Diagnostics.Any())
            {
                return new BoundGlobalScope(previous, binder.Diagnostics.ToImmutableArray(), null, null, ImmutableArray<FunctionSymbol>.Empty, ImmutableArray<EnumTypeSymbol>.Empty, ImmutableArray<VariableSymbol>.Empty, ImmutableArray<BoundStatement>.Empty);
            }

            var globalStatements = syntaxTrees.SelectMany(st => st.Root.Members)
                                              .OfType<GlobalStatementSyntax>();

            string? importedDll = null;

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

            if (previous != null)
            {
                diagnostics = diagnostics.InsertRange(0, previous.Diagnostics);
            }

            return new BoundGlobalScope(previous, diagnostics, mainFunction, scriptFunction, functions, enums, variables, statements.ToImmutable());
        }

        public static BoundProgram BindProgram(bool isScript, BoundProgram? previous, BoundGlobalScope globalScope)
        {
            var parentScope = CreateParentScope(globalScope);

            if (globalScope.Diagnostics.Any())
            {
                return new BoundProgram(previous, globalScope.Diagnostics, null, null, ImmutableDictionary<FunctionSymbol, BoundBlockStatement>.Empty);
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

                var binder = new Binder(isScript, parentScope, function);
                var body = binder.BindStatement(function.Declaration!.Body!);
                var loweredBody = Lowerer.Lower(function, body);

                if (function.ReturnType != TypeSymbol.Void && !ControlFlowGraph.AllPathsReturn(loweredBody))
                {
                    binder._diagnostics.ReportAllPathsMustReturn(function.Declaration.Identifier.Location);
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

            return new BoundProgram(previous, diagnostics.ToImmutable(), globalScope.MainFunction, globalScope.ScriptFunction, functionBodies.ToImmutable());
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

            var isExtern = syntax.CallingConventionKeyword != null;

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

            var callingConvention = syntax.CallingConventionKeyword?.Kind switch
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
                    scope.TryDeclareFunction(f);
                }

                foreach (var e in previous.Enums)
                {
                    scope.TryDeclareEnum(e);
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
                                              es.Expression.Kind == BoundNodeKind.ElementAssignmentExpression;

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
                case SyntaxKind.ElementAccessExpression: return BindElementAccessExpression((ElementAccessExpressionSyntax)syntax);
                case SyntaxKind.MemberAccessExpression: return BindMemberAccessExpression((MemberAccessExpressionSyntax)syntax);
                case SyntaxKind.MemberCallExpression: return BindMemberCallExpression((MemberCallExpressionSyntax)syntax);
                case SyntaxKind.CastExpression: return BindCastExpression((CastExpressionSyntax)syntax);
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

            var variable = BindVariableReference(syntax.IdentifierToken);
            if (variable == null)
                return new BoundErrorExpression(syntax);

            return new BoundVariableExpression(syntax, variable);
        }

        private BoundExpression BindAssignmentExpression(AssignmentExpressionSyntax syntax)
        {
            var boundTarget = BindExpression(syntax.Target);
            var boundExpression = BindExpression(syntax.Expression);

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
            var identifier = syntax.IdentifierToken.Text;

            if (boundTarget.Type == TypeSymbol.Error)
            {
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
            var boundExpression = BindExpression(syntax.Expression);
            var identifier = syntax.IdentifierToken.Text;

            if (boundExpression.Type == TypeSymbol.Error)
            {
                return new BoundErrorExpression(syntax);
            }

            var boundArguments = ImmutableArray.CreateBuilder<BoundExpression>();
            foreach (var argument in syntax.Arguments)
            {
                boundArguments.Add(BindExpression(argument));
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
                    return _scope.TryLookupSymbol(name) as EnumTypeSymbol;
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
