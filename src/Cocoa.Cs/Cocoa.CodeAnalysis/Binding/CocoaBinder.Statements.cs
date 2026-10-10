using Cocoa.CodeAnalysis.Lowering;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using CoreSyntax = Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using Cocoa.CodeAnalysis.Bound;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// Partial member surface of the binder.
    /// </summary>
    public partial class CocoaBinder
    {
        private BoundStatement BindErrorStatement(CoreSyntax.SyntaxNode syntax)
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
                case CoreSyntax.SyntaxKind.BlockStatement: return BindBlockStatement((BlockStatementSyntax)syntax);
                case CoreSyntax.SyntaxKind.VariableDeclaration: return BindVariableDeclaration((VariableDeclarationSyntax)syntax);
                case CoreSyntax.SyntaxKind.IfStatement: return BindIfStatement((IfStatementSyntax)syntax);
                case CoreSyntax.SyntaxKind.WhileStatement: return BindWhileStatement((WhileStatementSyntax)syntax);
                case CoreSyntax.SyntaxKind.DoWhileStatement: return BindDoWhileStatement((DoWhileStatementSyntax)syntax);
                case CoreSyntax.SyntaxKind.ForStatement: return BindForStatement((ForStatementSyntax)syntax);
                case CoreSyntax.SyntaxKind.ForRangeStatement: return BindForRangeStatement((ForRangeStatementSyntax)syntax);
                case CoreSyntax.SyntaxKind.ForeachStatement: return BindForeachStatement((ForeachStatementSyntax)syntax);
                case CoreSyntax.SyntaxKind.SwitchStatement: return BindSwitchStatement((SwitchStatementSyntax)syntax);
                case CoreSyntax.SyntaxKind.BreakStatement: return BindBreakStatement((BreakStatementSyntax)syntax);
                case CoreSyntax.SyntaxKind.ContinueStatement: return BindContinueStatement((ContinueStatementSyntax)syntax);
                case CoreSyntax.SyntaxKind.ReturnStatement: return BindReturnStatement((ReturnStatementSyntax)syntax);
                case CoreSyntax.SyntaxKind.ThrowStatement: return BindThrowStatement((ThrowStatementSyntax)syntax);
                case CoreSyntax.SyntaxKind.TryStatement: return BindTryStatement((TryStatementSyntax)syntax);
                case CoreSyntax.SyntaxKind.UsingStatement: return BindUsingStatement((UsingStatementSyntax)syntax);
                case CoreSyntax.SyntaxKind.LockStatement: return BindLockStatement((LockStatementSyntax)syntax);
                case CoreSyntax.SyntaxKind.CheckedStatement:
                case CoreSyntax.SyntaxKind.UncheckedStatement: return BindCheckedStatement((CheckedStatementSyntax)syntax);
                case CoreSyntax.SyntaxKind.YieldReturnStatement: return BindYieldReturnStatement((YieldReturnStatementSyntax)syntax);
                case CoreSyntax.SyntaxKind.YieldBreakStatement: return BindYieldBreakStatement((YieldBreakStatementSyntax)syntax);
                case CoreSyntax.SyntaxKind.ExpressionStatement: return BindExpressionStatement((ExpressionStatementSyntax)syntax);
                case CoreSyntax.SyntaxKind.LocalFunctionDeclaration: return BindLocalFunctionDeclaration((LocalFunctionDeclarationStatementSyntax)syntax);
                default:
                    // 1b/B8：解析器 panic 恢复合成的意外节点报诊断 + Nop 降级，而非编译器崩溃
                    _diagnostics.ReportError(syntax.Location, $"意外的语句语法 {syntax.Kind}。");
                    return new BoundNopStatement(syntax);
            }
        }

        private BoundStatement BindBlockStatement(BlockStatementSyntax syntax)
        {
            var statements = ImmutableArray.CreateBuilder<BoundStatement>();
            _scope = new BoundScope(_scope);

            var usingVariables = new List<(VariableSymbol variable, BoundExpression initializer)>();

            foreach (var statementSyntax in syntax.Statements)
            {
                var statement = BindStatement(statementSyntax);
                statements.Add(statement);

                // Track using declarations for try/finally lowering
                if (statementSyntax is VariableDeclarationSyntax varDecl && varDecl.IsUsingDeclaration &&
                    statement is BoundVariableDeclaration boundVar && boundVar.Initializer.Type != TypeSymbol.Error)
                {
                    // IDisposable 类型检查
                    ValidateUsingResourceType(varDecl, boundVar.Variable.Type);
                    usingVariables.Add((boundVar.Variable, boundVar.Initializer));
                }
            }

            _scope = _scope.Parent!;

            var block = new BoundBlockStatement(syntax, statements.ToImmutable());

            // Wrap in try/finally if there are using declarations
            if (usingVariables.Count > 0)
            {
                return WrapWithUsingFinally(syntax, block, usingVariables);
            }

            return block;
        }

        /// <summary>
        /// using 资源类型检查：类型必须实现 IDisposable 或有 Dispose() 方法
        /// </summary>
        private void ValidateUsingResourceType(VariableDeclarationSyntax syntax, TypeSymbol resourceType)
        {
            if (resourceType == TypeSymbol.Error || resourceType == TypeSymbol.Null)
                return;

            if (resourceType is not NamedTypeSymbol namedType)
                return;

            // 检查是否实现 IDisposable
            var disposable = LookupType("IDisposable") as NamedTypeSymbol;
            if (disposable != null && namedType.GetAllInterfaces().Contains(disposable))
                return;

            // 检查是否有 Dispose() 方法（模式匹配）
            if (namedType.GetMethod("Dispose") != null)
                return;

            // 都没有 → 报错
            _diagnostics.ReportError(syntax.Location,
                $"Type '{resourceType}' cannot be used in a using statement " +
                $"because it does not implement IDisposable and has no Dispose() method.");
        }

        private BoundStatement WrapWithUsingFinally(CoreSyntax.SyntaxNode syntax, BoundBlockStatement block,
            List<(VariableSymbol variable, BoundExpression initializer)> usingVars)
        {
            var finallyStatements = ImmutableArray.CreateBuilder<BoundStatement>();

            // 查找 IDisposable 接口
            var disposable = LookupType("IDisposable") as NamedTypeSymbol;

            foreach (var (variable, _) in usingVars)
            {
                var varExpr = BoundNodeFactory.Variable(syntax, variable);
                var notNull = BoundNodeFactory.Binary(syntax, varExpr, CoreSyntax.SyntaxKind.BangEqualsToken,
                    new BoundLiteralExpression(syntax, null!, TypeSymbol.Null));

                BoundExpression disposeReceiver;

                // 6e-M35：接收者类型自身有 Dispose 方法 → 直接调用（native 虚分派/IL callvirt 可处理具体类方法）；
                // 否则才用 IDisposable 接口转换（facade 接口抽象方法 native 无 IR/imdispatch）。
                var hasOwnDispose = variable.Type is NamedTypeSymbol own && own.GetMethod("Dispose") != null;
                if (disposable != null && !hasOwnDispose &&
                    variable.Type is NamedTypeSymbol namedType &&
                    namedType.GetAllInterfaces().Contains(disposable))
                {
                    disposeReceiver = new BoundConversionExpression(syntax, disposable, varExpr);
                }
                else
                {
                    // 模式匹配：直接调用 x.Dispose()
                    disposeReceiver = varExpr;
                }

                // IDisposable 接收者（转换后）/模式匹配接收者上解析 Dispose 方法——6e-M35：Method 为空时
                // 发射器/求值器无法定位成员（Handle 族 using 触发），沿类型链查询补全
                var disposeCall = BindDisposeCall(syntax, disposeReceiver);

                var ifTrue = new BoundBlockStatement(syntax, ImmutableArray.Create<BoundStatement>(
                    new BoundExpressionStatement(syntax, disposeCall)));
                var ifStmt = new BoundIfStatement(syntax, notNull, ifTrue, null);
                finallyStatements.Add(ifStmt);
            }

            var finallyBlock = new BoundBlockStatement(syntax, finallyStatements.ToImmutable());
            return new BoundTryStatement(syntax, block, ImmutableArray<BoundCatchClause>.Empty, finallyBlock);
        }

        private BoundExpression BindDisposeCall(CoreSyntax.SyntaxNode syntax, BoundExpression receiver)
        {
            // Create receiver.Dispose() as a BoundMemberCallExpression
            // 6e-M35：解析 Dispose 方法（沿 receiver 类型链）——Method 为空时发射器/求值器无法定位成员
            FunctionSymbol? disposeMethod = null;
            if (receiver.Type is NamedTypeSymbol disposeType)
            {
                disposeMethod = disposeType.GetMethod("Dispose");
            }

            return new BoundMemberCallExpression(syntax, receiver, "Dispose",
                ImmutableArray<BoundExpression>.Empty, TypeSymbol.Void, disposeMethod);
        }

        private BoundStatement BindUsingStatement(UsingStatementSyntax syntax)
        {
            _scope = new BoundScope(_scope);

            // Bind resource declaration
            var resource = BindStatement(syntax.Resource);

            // Validate resource type for IDisposable
            if (resource is BoundVariableDeclaration boundVar && boundVar.Initializer.Type != TypeSymbol.Error)
            {
                if (syntax.Resource is VariableDeclarationSyntax varDecl)
                    ValidateUsingResourceType(varDecl, boundVar.Variable.Type);
            }

            // Bind body
            var bodyBlock = BindBlockStatement(syntax.Body);

            _scope = _scope.Parent!;

            // Collect using variable for try/finally lowering
            var usingVariables = new List<(VariableSymbol variable, BoundExpression initializer)>();
            if (resource is BoundVariableDeclaration bv && bv.Initializer.Type != TypeSymbol.Error)
            {
                usingVariables.Add((bv.Variable, bv.Initializer));
            }

            // Flatten: resource + body statements into one block
            var statements = ImmutableArray.CreateBuilder<BoundStatement>();
            statements.Add(resource);
            if (bodyBlock is BoundBlockStatement bb)
            {
                foreach (var s in bb.Statements)
                    statements.Add(s);
            }
            else
            {
                statements.Add(bodyBlock);
            }

            var block = new BoundBlockStatement(syntax, statements.ToImmutable());
            if (usingVariables.Count > 0)
            {
                var tryFinally = WrapWithUsingFinally(syntax, block, usingVariables);
                return new BoundBlockStatement(syntax, ImmutableArray.Create<BoundStatement>(tryFinally));
            }

            return block;
        }

        private BoundStatement BindLockStatement(LockStatementSyntax syntax)
        {
            // lock (expr) { body }
            // If expr is System.Threading.Lock: try { expr.Enter(); body } finally { expr.Exit(); }
            // Else: try { Monitor.Enter(expr); body } finally { Monitor.Exit(expr); }

            var boundExpression = BindExpression(syntax.Expression);
            if (boundExpression.Type == TypeSymbol.Error)
                return new BoundNopStatement(syntax);

            var boundBody = BindStatement(syntax.Body);
            var bodyStatements = ImmutableArray.CreateBuilder<BoundStatement>();

            var isLockType = boundExpression.Type is NamedTypeSymbol namedType && namedType.FullName == "System.Threading.Lock";

            if (isLockType)
            {
                var lockType = boundExpression.Type as NamedTypeSymbol;
                var enterMethod = lockType?.GetMethod("Enter");
                var enterCall = new BoundMemberCallExpression(syntax, boundExpression, "Enter",
                    ImmutableArray<BoundExpression>.Empty, TypeSymbol.Void, enterMethod);
                bodyStatements.Add(new BoundExpressionStatement(syntax, enterCall));
            }
            else
            {
                // Monitor.Enter(expr)
                var monitorType = LookupType("System.Threading.Monitor") as NamedTypeSymbol;
                if (monitorType != null)
                {
                    var enterMethod = monitorType.GetMethod("Enter");
                    if (enterMethod != null)
                    {
                        var enterCall = new BoundCallExpression(syntax, enterMethod,
                            ImmutableArray.Create(boundExpression));
                        bodyStatements.Add(new BoundExpressionStatement(syntax, enterCall));
                    }
                }
            }

            if (boundBody is BoundBlockStatement block)
            {
                foreach (var s in block.Statements)
                    bodyStatements.Add(s);
            }
            else
            {
                bodyStatements.Add(boundBody);
            }

            var tryBlock = new BoundBlockStatement(syntax.Body, bodyStatements.ToImmutable());

            // Finally block
            var finallyStatements = ImmutableArray.CreateBuilder<BoundStatement>();
            if (isLockType)
            {
                var lockType = boundExpression.Type as NamedTypeSymbol;
                var exitMethod = lockType?.GetMethod("Exit");
                var exitCall = new BoundMemberCallExpression(syntax, boundExpression, "Exit",
                    ImmutableArray<BoundExpression>.Empty, TypeSymbol.Void, exitMethod);
                finallyStatements.Add(new BoundExpressionStatement(syntax, exitCall));
            }
            else
            {
                var monitorType = LookupType("System.Threading.Monitor") as NamedTypeSymbol;
                if (monitorType != null)
                {
                    var exitMethod = monitorType.GetMethod("Exit");
                    if (exitMethod != null)
                    {
                        var exitCall = new BoundCallExpression(syntax, exitMethod,
                            ImmutableArray.Create(boundExpression));
                        finallyStatements.Add(new BoundExpressionStatement(syntax, exitCall));
                    }
                }
            }

            var finallyBlock = new BoundBlockStatement(syntax.Body,
                finallyStatements.Count > 0 ? finallyStatements.ToImmutable() :
                ImmutableArray.Create<BoundStatement>(new BoundNopStatement(syntax)));

            return new BoundTryStatement(syntax, tryBlock,
                ImmutableArray<BoundCatchClause>.Empty, finallyBlock);
        }

        private BoundStatement BindCheckedStatement(CheckedStatementSyntax syntax)
        {
            // 算术溢出语义在发射期决定（IL 选 add.ovf 等），绑定期只把上下文标记随树带下去。
            // 栈式记录嵌套：内层 checked/unchecked 覆盖外层（C# 同）。
            var isChecked = syntax.Keyword.Kind == CoreSyntax.SyntaxKind.CheckedKeyword;
            _checkedStack.Push(isChecked);
            try
            {
                var body = BindStatement(syntax.Body);
                return new BoundCheckedStatement(syntax, body, isChecked);
            }
            finally
            {
                _checkedStack.Pop();
            }
        }

        /// <summary>当前是否处于 checked 溢出检查上下文（最近的 checked/unchecked 声明为 checked 时）。</summary>
        private bool IsCheckedContext => _checkedStack.Count > 0 && _checkedStack.Peek();

        private BoundStatement BindYieldReturnStatement(YieldReturnStatementSyntax syntax)
        {
            var expression = BindExpression(syntax.Expression);
            return new BoundYieldReturnStatement(syntax, expression);
        }

        private BoundStatement BindYieldBreakStatement(YieldBreakStatementSyntax syntax)
        {
            return new BoundYieldBreakStatement(syntax);
        }

        private BoundStatement BindVariableDeclaration(VariableDeclarationSyntax syntax)
        {
            var isReadOnly = syntax.Keyword?.Kind == CoreSyntax.SyntaxKind.LetKeyword ||
                             syntax.Keyword?.Kind == CoreSyntax.SyntaxKind.ConstKeyword;
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
                if (syntax.Keyword?.Kind == CoreSyntax.SyntaxKind.LetKeyword ||
                    syntax.Keyword?.Kind == CoreSyntax.SyntaxKind.ConstKeyword)
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

            // object 装箱目标：初始值经装箱为堆对象，原始值常量不可传播（o≡7 在 native 发射会
            // 以裸值替代 box 指针 → unbox 解引用崩溃；IL 端同理）。引用型 string/类常量可传播（引用即值）。
            var constant = variableType == NamedTypeSymbol.SystemObject ? null : initializer.ConstantValue;
            var variable = BindVariableDeclaration(syntax.Identifier, isReadOnly, variableType, constant);
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

            if (type is NamedTypeSymbol { TypeKind: TypeKind.Enum })
            {
                return 0;
            }

            if (type == TypeSymbol.String || (type is NamedTypeSymbol && !type.IsPrimitiveValueType) || type.ElementType != null)
            {
                return null!;
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

            return new BoundBaseExpression(syntax, _currentClass.BaseType!);
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

            DeclarePatternVariables(condition);

            var thenStatement = BindStatement(syntax.ThenStatement);
            var elseStatement = syntax.ElseClause == null ? null : BindStatement(syntax.ElseClause.ElseStatement);

            return new BoundIfStatement(syntax, condition, thenStatement, elseStatement);
        }

        private void DeclarePatternVariables(BoundExpression expression)
        {
            if (expression is BoundDeclarationPattern declarationPattern)
            {
                _scope.TryDeclareVariable(declarationPattern.Variable);
            }
            else if (expression is BoundLogicalPattern logicalPattern)
            {
                if (logicalPattern.Left != null)
                    DeclarePatternVariables(logicalPattern.Left);
                if (logicalPattern.Right != null)
                    DeclarePatternVariables(logicalPattern.Right);
                if (logicalPattern.IsUnary && logicalPattern.Operand != null)
                    DeclarePatternVariables(logicalPattern.Operand);
            }
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
            _scope = new BoundScope(_scope);

            var initStatements = ImmutableArray.CreateBuilder<BoundStatement>();
            if (syntax.InitDeclaration != null)
            {
                initStatements.Add(BindStatement(syntax.InitDeclaration));
            }

            foreach (var initializer in syntax.Initializers)
            {
                initStatements.Add(new BoundExpressionStatement(syntax, BindExpression(initializer)));
            }

            var condition = syntax.Condition == null ? null : BindExpression(syntax.Condition, TypeSymbol.Boolean);
            var body = BindLoopBody(syntax.Body, out var breakLabel, out var continueLabel);

            var incrementorExpressions = ImmutableArray.CreateBuilder<BoundExpression>();
            foreach (var incrementor in syntax.Incrementors)
            {
                incrementorExpressions.Add(BindExpression(incrementor));
            }

            _scope = _scope.Parent!;

            // C 风格 for 在绑定期脱糖为既有的纯循环节点：
            // {
            //     init...
            //     while (true)
            //     {
            //         if (condition) { } else break;
            //         body
            //         continue:
            //         update...
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

            foreach (var incrementor in incrementorExpressions)
            {
                whileBody.Add(new BoundExpressionStatement(syntax, incrementor));
            }

            var whileStatement = new BoundWhileStatement(
                syntax,
                new BoundLiteralExpression(syntax, true),
                new BoundBlockStatement(syntax, whileBody.ToImmutable()),
                breakLabel,
                whileContinueLabel);

            initStatements.Add(whileStatement);

            return new BoundBlockStatement(syntax, initStatements.ToImmutable());
        }

        private BoundStatement BindForRangeStatement(ForRangeStatementSyntax syntax)
        {
            var lowerBound = BindExpression(syntax.LowerBound, TypeSymbol.Int32);
            var upperBound = BindExpression(syntax.UpperBound, TypeSymbol.Int32);

            // 方向（Y-A4-2）：两界为编译期常量时按比较自动定方向（lower > upper → 降序，如 `10 to 1`）；
            // 否则以显式 step 符号为准（A4-1：负 step 降序）；缺省升序。
            var lowerConst = lowerBound.ConstantValue?.Value is int lv ? lv : (int?)null;
            var upperConst = upperBound.ConstantValue?.Value is int uv ? uv : (int?)null;
            var autoDescending = lowerConst != null && upperConst != null && lowerConst > upperConst;

            // 可选步长：仅支持常量非零整数——按幅值（方向由边界比较 / 负号决定，内部以带符号 step 表达）
            BoundExpression? step = null;
            if (syntax.Step != null)
            {
                step = BindExpression(syntax.Step, TypeSymbol.Int32);
                if (step.ConstantValue == null ||
                    step.ConstantValue.Value is not int stepValue ||
                    stepValue == 0)
                {
                    _diagnostics.ReportError(syntax.Step.Location, "for 循环的 step 必须为常量非零整数。");
                }
                else
                {
                    var magnitude = Math.Abs(stepValue);
                    var descending = autoDescending || stepValue < 0;
                    step = new BoundLiteralExpression(syntax.Step, descending ? -magnitude : magnitude);
                }
            }
            else if (autoDescending)
            {
                // 降序缺省步长 → -1
                step = new BoundLiteralExpression(syntax.LowerBound, -1);
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

            return new BoundForRangeStatement(syntax, variable, lowerBound, upperBound, step, body, breakLabel, continueLabel);
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
            var counterToken = new CoreSyntax.SyntaxToken(syntax.SyntaxTree, CoreSyntax.SyntaxKind.IdentifierToken, syntax.Keyword.Span.Start, counterName, counterName, ImmutableArray<CoreSyntax.SyntaxTrivia>.Empty, ImmutableArray<CoreSyntax.SyntaxTrivia>.Empty);
            var counter = BindVariableDeclaration(counterToken, isReadOnly: false, TypeSymbol.Int32);

            // 隐藏集合暂存 __c（1b/B1）：集合表达式只求值一次——旧实现把 collection 节点同时
            // 嵌入条件 Length 访问与体内元素访问，带副作用的集合（如 GetItems()）每迭代重复求值
            var collectionTempName = $"__foreach_c{_labelCounter}";
            var collectionTempToken = new CoreSyntax.SyntaxToken(syntax.SyntaxTree, CoreSyntax.SyntaxKind.IdentifierToken, syntax.Keyword.Span.Start, collectionTempName, collectionTempName, ImmutableArray<CoreSyntax.SyntaxTrivia>.Empty, ImmutableArray<CoreSyntax.SyntaxTrivia>.Empty);
            var collectionDecl = BindVariableDeclaration(collectionTempToken, isReadOnly: true, collection.Type);
            var collectionVar = BoundNodeFactory.Variable(syntax, collectionDecl);

            var breakLabel = new BoundLabel($"break{_labelCounter}");
            var continueLabel = new BoundLabel($"continue{_labelCounter}");
            var whileContinueLabel = new BoundLabel($"whilecontinue{_labelCounter}");

            var loopBody = ImmutableArray.CreateBuilder<BoundStatement>();

            // 内层作用域：循环变量 x（只读，每迭代新建，C# 语义）
            _scope = new BoundScope(_scope);
            var loopVar = BindVariableDeclaration(syntax.Identifier, isReadOnly: true, elementType);
            var elementAccess = new BoundElementAccessExpression(syntax, elementType, collectionVar, BoundNodeFactory.Variable(syntax, counter));
            loopBody.Add(BoundNodeFactory.VariableDeclaration(syntax, loopVar, elementAccess));

            _loopStack.Push((breakLabel, continueLabel));
            loopBody.Add(BindStatement(syntax.Body));
            _loopStack.Pop();

            _scope = _scope.Parent!;

            loopBody.Add(BoundNodeFactory.Label(syntax, continueLabel));
            loopBody.Add(BoundNodeFactory.Increment(syntax, BoundNodeFactory.Variable(syntax, counter)));

            var lengthAccess = new BoundMemberAccessExpression(syntax, TypeSymbol.Int32, collectionVar, "Length");
            var condition = BoundNodeFactory.Binary(syntax,
                BoundNodeFactory.Variable(syntax, counter),
                CoreSyntax.SyntaxKind.LessToken,
                lengthAccess);

            var whileStatement = BoundNodeFactory.While(syntax, condition,
                new BoundBlockStatement(syntax, loopBody.ToImmutable()), breakLabel, whileContinueLabel);

            var collectionInit = BoundNodeFactory.VariableDeclaration(syntax, collectionDecl, collection);
            var counterInit = BoundNodeFactory.VariableDeclaration(syntax, counter, BoundNodeFactory.Literal(syntax, 0));

            _scope = _scope.Parent!;

            return BoundNodeFactory.Block(syntax, collectionInit, counterInit, whileStatement);
        }

        /// <summary>
        /// 集合是否可枚举（6e-M20 G6）：实现 System.Collections.Generic.IEnumerable&lt;T&gt; 实例化
        /// 且存在无参 GetEnumerator() 方法 → 返回其具体枚举器类。
        /// </summary>
        private static NamedTypeSymbol? FindEnumeratorClass(TypeSymbol collectionType)
        {
            if (collectionType is not NamedTypeSymbol classType || classType.IsInterface)
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
            if (getEnumerator.ReturnType is NamedTypeSymbol enumType &&
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
                    return getEnumerator.ReturnType as NamedTypeSymbol;
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
        private BoundStatement BindEnumeratorForeach(ForeachStatementSyntax syntax, BoundExpression collection, NamedTypeSymbol enumeratorClass)
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
            var enumToken = new CoreSyntax.SyntaxToken(syntax.SyntaxTree, CoreSyntax.SyntaxKind.IdentifierToken, syntax.Keyword.Span.Start, $"__foreach_e{counter}", $"__foreach_e{counter}", ImmutableArray<CoreSyntax.SyntaxTrivia>.Empty, ImmutableArray<CoreSyntax.SyntaxTrivia>.Empty);
            var enumeratorDecl = BindVariableDeclaration(enumToken, isReadOnly: false, enumeratorClass);

            var breakLabel = new BoundLabel($"break{counter}");
            var continueLabel = new BoundLabel($"continue{counter}");
            var whileContinueLabel = new BoundLabel($"whilecontinue{counter}");

            var enumVariable = BoundNodeFactory.Variable(syntax, enumeratorDecl);

            // init：collection.GetEnumerator()
            var getEnumeratorMethod = ((NamedTypeSymbol)collection.Type).GetMethod("GetEnumerator")!;
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
            var boundValue = BindExpression(syntax.Expression);

            // 隐藏判别式暂存（1b/B1）：判别式只求值一次——旧实现把 value 节点嵌入每个 case 的
            // 等值比较，switch (GetVal()) {...} 会对每个 case 各调一次 GetVal()
            _labelCounter++;
            var switchTempName = $"__switch_v{_labelCounter}";
            var switchTempToken = new CoreSyntax.SyntaxToken(syntax.SyntaxTree, CoreSyntax.SyntaxKind.IdentifierToken, syntax.Keyword.Span.Start, switchTempName, switchTempName, ImmutableArray<CoreSyntax.SyntaxTrivia>.Empty, ImmutableArray<CoreSyntax.SyntaxTrivia>.Empty);
            var switchTempDecl = BindVariableDeclaration(switchTempToken, isReadOnly: true, boundValue.Type);
            var value = BoundNodeFactory.Variable(syntax, switchTempDecl);

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
                    var equality = BoundNodeFactory.Binary(syntax, value, CoreSyntax.SyntaxKind.EqualsEqualsToken, caseValue);
                    condition = condition == null
                        ? equality
                        : BoundNodeFactory.Binary(syntax, condition, CoreSyntax.SyntaxKind.PipePipeToken, equality);
                }

                pendingValues.Clear();

                if (caseClause.WhenCondition != null)
                {
                    var whenCondition = BindExpression(caseClause.WhenCondition, TypeSymbol.Boolean);
                    condition = condition == null
                        ? whenCondition
                        : BoundNodeFactory.Binary(syntax, condition, CoreSyntax.SyntaxKind.AmpersandAmpersandToken, whenCondition);
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
                    var equality = BoundNodeFactory.Binary(syntax, value, CoreSyntax.SyntaxKind.EqualsEqualsToken, caseValue);
                    trailingCondition = trailingCondition == null
                        ? equality
                        : BoundNodeFactory.Binary(syntax, trailingCondition, CoreSyntax.SyntaxKind.PipePipeToken, equality);
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
            result.Add(BoundNodeFactory.VariableDeclaration(syntax, switchTempDecl, boundValue));
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

            if (last.Kind is CoreSyntax.SyntaxKind.BreakStatement or CoreSyntax.SyntaxKind.ReturnStatement or CoreSyntax.SyntaxKind.ContinueStatement)
            {
                return;
            }

            _diagnostics.ReportError(last.Location, "switch 节体必须以 break/return/continue 结尾（不支持 fall-through）。");
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
            var exceptionRoot = LookupType("Exception") as NamedTypeSymbol;
            if (exceptionRoot == null)
            {
                return true; // 无 Exception 根（stdlib 缺失）时不额外报错
            }

            for (var current = type as NamedTypeSymbol; current != null; current = current.BaseType)
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
            if (syntax.Expression.Kind == CoreSyntax.SyntaxKind.AssignmentExpression)
            {
                var subscription = TryBindEventSubscription((AssignmentExpressionSyntax)syntax.Expression);
                if (subscription != null)
                {
                    return subscription;
                }
            }

            // 元组解构（语言后置件）：`(a, b) = expr` —— 按 ItemN 逐字段赋给变量（多赋值语句块）。
            if (syntax.Expression is AssignmentExpressionSyntax deconstructAssign && deconstructAssign.Target is TupleExpressionSyntax)
            {
                return BindTupleDeconstruction(deconstructAssign);
            }

            if (syntax.Expression.Kind == CoreSyntax.SyntaxKind.CallExpression && _currentClass != null)
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

        /// <summary>元组解构（语言后置件）：`(a, b) = expr` —— 右侧元组按 ItemN 逐字段赋给变量，生成多赋值语句块。</summary>
        private BoundStatement BindTupleDeconstruction(AssignmentExpressionSyntax assignment)
        {
            var target = (TupleExpressionSyntax)assignment.Target;
            var value = BindExpression(assignment.Expression, canBeVoid: true);
            var tupleType = value.Type as NamedTypeSymbol;
            if (tupleType == null)
            {
                _diagnostics.ReportError(assignment.Expression.Location, "解构右侧必须是元组值。");
                return new BoundNopStatement(assignment);
            }

            var statements = ImmutableArray.CreateBuilder<BoundStatement>();
            var instanceFields = tupleType.Fields.Where(f => !f.IsStatic).ToArray();
            for (var i = 0; i < target.Elements.Count; i++)
            {
                var elementSyntax = target.Elements[i];
                if (elementSyntax is not NameExpressionSyntax nameExpression)
                {
                    _diagnostics.ReportError(elementSyntax.Location, "解构目标须为变量名。");
                    continue;
                }

                if (_scope.TryLookupSymbol(nameExpression.IdentifierToken.Text) is not VariableSymbol variable)
                {
                    _diagnostics.ReportError(elementSyntax.Location, $"未定义变量 '{nameExpression.IdentifierToken.Text}'。");
                    continue;
                }

                // 按位置取实例字段：元组（Item1..ItemN）与 record（位置参数字段序）统一按声明序取值。
                if (i >= instanceFields.Length)
                {
                    _diagnostics.ReportError(elementSyntax.Location, $"记录没有第 {i + 1} 个字段。");
                    continue;
                }

                var field = instanceFields[i];
                var itemAccess = new BoundMemberAccessExpression(assignment, field.Type, value, field.Name, field);
                statements.Add(new BoundExpressionStatement(assignment, new BoundAssignmentExpression(assignment, variable, itemAccess)));
            }

            return new BoundBlockStatement(assignment, statements.ToImmutable());
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
                case CoreSyntax.SyntaxKind.ParenthesizedExpression: return BindParenthesizedExpression((ParenthesizedExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.LiteralExpression: return BindLiteralExpression((LiteralExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.NameExpression: return BindNameExpression((NameExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.AssignmentExpression: return BindAssignmentExpression((AssignmentExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.UnaryExpression: return BindUnaryExpression((UnaryExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.PostfixIncrementExpression: return BindPostfixIncrementExpression((PostfixIncrementExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.BinaryExpression: return BindBinaryExpression((BinaryExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.ConditionalExpression: return BindConditionalExpression((ConditionalExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.CallExpression: return BindCallExpression((CallExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.ArrayCreationExpression: return BindArrayCreationExpression((ArrayCreationExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.ObjectCreationExpression: return BindObjectCreationExpression((ObjectCreationExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.WithExpression: return BindWithExpression((WithExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.CollectionExpression: return BindCollectionExpression((CollectionExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.ElementAccessExpression: return BindElementAccessExpression((ElementAccessExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.MemberAccessExpression: return BindMemberAccessExpression((MemberAccessExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.MemberCallExpression: return BindMemberCallExpression((MemberCallExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.CastExpression: return BindCastExpression((CastExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.ThisExpression: return BindThisExpression((ThisExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.BaseExpression: return BindBaseExpression((BaseExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.InterpolatedStringExpression: return BindInterpolatedStringExpression((InterpolatedStringExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.IsExpression: return BindIsExpression((IsExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.AsExpression: return BindAsExpression((AsExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.NameofExpression: return BindNameofExpression((NameofExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.TypeOperatorExpression: return BindTypeOperatorExpression((TypeOperatorExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.ConditionalAccessExpression: return BindConditionalAccessExpression((ConditionalAccessExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.LambdaExpression: return BindLambdaExpression((LambdaExpressionSyntax)syntax, expectedType: null);
                case CoreSyntax.SyntaxKind.ByRefArgument: return BindByRefArgument((ByRefArgumentExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.NamedArgument: return BindNamedArgument((NamedArgumentExpressionSyntax)syntax);
                case CoreSyntax.SyntaxKind.TupleExpression: return BindTupleExpression((TupleExpressionSyntax)syntax);

                default:
                    // 1b/B8：意外的表达式语法报诊断 + ErrorExpression 降级，而非编译器崩溃
                    _diagnostics.ReportError(syntax.Location, $"意外的表达式语法 {syntax.Kind}。");
                    return new BoundErrorExpression(syntax);
            }
        }

        /// <summary>byref 实参绑定（6e-M23 R3）：实参须为可赋值 lvalue——变量/实例或静态字段（非只读）/数组元素。
        /// `out var v` 声明式实参返回占位，类型由 CheckByRefArgument 按形参推断（对齐 C# out var）。</summary>
        private BoundExpression BindNamedArgument(NamedArgumentExpressionSyntax syntax) => BindExpression(syntax.Expression);

        private BoundExpression BindByRefArgument(ByRefArgumentExpressionSyntax syntax)
        {
            if (syntax.Expression is DeclarationExpressionSyntax)
            {
                return new BoundByRefArgument(syntax, new BoundErrorExpression(syntax.Expression), syntax.IsRef);
            }

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

            // out var v：声明式实参——用形参类型声明局部变量（类型推断），再作 out 实参（copy-out 由发射器处理）
            if (wrapped.Syntax is ByRefArgumentExpressionSyntax byRefSyntax &&
                byRefSyntax.Expression is DeclarationExpressionSyntax decl)
            {
                var variable = BindVariableDeclaration(decl.Identifier, isReadOnly: false, parameter.Type);
                return new BoundByRefArgument(wrapped.Syntax, new BoundVariableExpression(decl.Identifier, variable), wrapped.IsRef);
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
            // Y A2-F1：产"高 Bound"（文本段 + 已绑定洞），由共享规范化 pass 降至 format/拼接。
            // 洞的绑定、对齐常量校验、无格式洞的 string 转换仍在 CocoaBinder 完成（行为与旧内联一致）。
            var items = ImmutableArray.CreateBuilder<BoundInterpolationItem>();
            foreach (var content in syntax.Contents)
            {
                if (content is InterpolatedStringTextSyntax text)
                {
                    var value = (string)text.TextToken.Value!;
                    items.Add(new BoundInterpolationItem(new BoundLiteralExpression(text, value), isHole: false, width: null, format: null, syntax: text));
                }
                else if (content is InterpolationSyntax interpolation)
                {
                    var bound = BindExpression(interpolation.Expression);

                    if (interpolation.Alignment != null || interpolation.FormatToken != null)
                    {
                        int? width = null;
                        if (interpolation.Alignment != null)
                        {
                            var boundAlignment = BindExpression(interpolation.Alignment);
                            if (!TryGetIntConstant(boundAlignment, out var intValue))
                            {
                                _diagnostics.ReportError(interpolation.Alignment.Location, "插值洞的对齐宽度必须为整数常量。");
                                items.Add(new BoundInterpolationItem(new BoundErrorExpression(interpolation.Alignment), isHole: true, width: null, format: FormatOf(interpolation), syntax: interpolation));
                                continue;
                            }

                            width = intValue;
                        }

                        items.Add(new BoundInterpolationItem(bound, isHole: true, width: width, format: FormatOf(interpolation), syntax: interpolation));
                    }
                    else
                    {
                        var converted = BindConversion(interpolation.Expression.Location, bound, TypeSymbol.String, allowExplicit: true);
                        items.Add(new BoundInterpolationItem(converted, isHole: true, width: null, format: null, syntax: interpolation));
                    }
                }
            }

            if (items.Count == 1 && !items[0].IsHole)
            {
                // 纯单个文本段：直接返回字面量（与旧 AppendInterpolation(left==null → right) 一致）
                return items[0].Value;
            }

            return new BoundInterpolatedStringExpression(syntax, items.ToImmutable());
        }

        private static string? FormatOf(InterpolationSyntax interpolation)
        {
            return interpolation.FormatToken == null ? null : (string)interpolation.FormatToken.Value!;
        }

        private BoundExpression BindParenthesizedExpression(ParenthesizedExpressionSyntax syntax)
        {
            return BindExpression(syntax.Expression);
        }

        private BoundExpression BindLiteralExpression(LiteralExpressionSyntax syntax)
        {
            // 6e-M19 M5-a：null 字面量 → Null 类型（绑定期经 BindConversion 落到目标引用型）
            if (syntax.LiteralToken.Kind == CoreSyntax.SyntaxKind.NullKeyword)
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
    }
}
