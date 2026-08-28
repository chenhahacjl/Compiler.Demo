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
    /// Partial member surface of the binder.
    /// </summary>
    internal sealed partial class Binder
    {
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
            var targetClass = target as NamedTypeSymbol;

            // `is/as String` 解析为 System.String 承载类（facade/外部）→ 归一为基元 string
            if (targetClass != null && targetClass.FullName == "System.String")
            {
                target = TypeSymbol.String;
                targetClass = null;
            }

            if ((targetClass != null && targetClass.IsInterface) || target.ElementType != null ||
                (targetClass == null && target != TypeSymbol.String) ||
                (targetClass != null && target.IsPrimitiveValueType))
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

            // 接收者约束：类（含接口变量）/string/null 字面量之外拒绝（值类型/基元不支持 is/as）
            if (receiverType != TypeSymbol.Null && receiverType != TypeSymbol.String &&
                receiverType != TypeSymbol.Any && receiverType.ElementType == null &&
                (!(receiverType is NamedTypeSymbol) || receiverType.IsPrimitiveValueType))
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

            var receiverClass = (NamedTypeSymbol)receiverType;
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
            if (type is NamedTypeSymbol { IsGenericDefinition: true })
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

            var definition = LookupType(identifier.Text) as NamedTypeSymbol;
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
            if (!string.IsNullOrEmpty(prefix) && LookupType(prefix!) is NamedTypeSymbol staticType)
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

            if (boundReceiver.Type is NamedTypeSymbol receiverClass)
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
        private void ValidateTypeArgumentConstraints(TextLocation errorLocation, NamedTypeSymbol definition, ImmutableArray<TypeSymbol> arguments)
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
                    if (constraint is not NamedTypeSymbol constraintClass)
                    {
                        _diagnostics.ReportError(errorLocation, $"约束 '{constraint.Name}' 不是受支持的约束形式（支持接口/基类）。");
                        continue;
                    }

                    var constraintName = constraintClass.FullName;

                    if (argument is not NamedTypeSymbol argumentClass)
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
            if (type is NamedTypeSymbol { IsValueType: false } || type is TypeParameterSymbol)
            {
                return true;
            }

            if (type is ArrayTypeSymbol)
            {
                return true;
            }

            return type == TypeSymbol.String || type == TypeSymbol.Any;
        }

        /// <summary>值类型判定（where T: struct，6e-M22 C1）：基元数值全集 + bool + char + enum + 用户 struct；其数组形式同视为值类型。</summary>
        private static bool IsValueType(TypeSymbol type)
        {
            if (type is NamedTypeSymbol { TypeKind: TypeKind.Enum } ||
                type is NamedTypeSymbol { TypeKind: TypeKind.Struct })
            {
                return true;
            }

            if (type.ElementType != null && IsValueType(type.ElementType))
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
            var enumToken = new SyntaxToken(syntax.SyntaxTree, SyntaxKind.IdentifierToken, syntax.Keyword.Span.Start, $"__foreach_e{counter}", $"__foreach_e{counter}", ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty);
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
    }
}
