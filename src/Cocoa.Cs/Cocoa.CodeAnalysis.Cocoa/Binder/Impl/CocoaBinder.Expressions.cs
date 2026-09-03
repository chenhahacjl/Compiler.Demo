using Cocoa.CodeAnalysis.Lowering;
using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Cocoa.Syntax;
using SSyntax = Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Cocoa.CodeAnalysis.Cocoa.Binding
{
    /// <summary>
    /// Partial member surface of the binder.
    /// </summary>
    internal partial class CocoaBinder
    {
        private BoundExpression CreateFunctionValue(SSyntax.SyntaxNode syntax, BoundExpression? receiver, FunctionSymbol function)
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

            // 体绑定：子作用域声明参数（沿用当前 CocoaBinder 上下文——类/静态/别名等语义一致）
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
            NamedTypeSymbol? environmentClass = null;

            if (captures.Count > 0)
            {
                environmentOwner = _environmentOwner ?? _function;

                if (environmentOwner == null || environmentOwner.IsLambda)
                {
                    _diagnostics.ReportError(syntax.Location, "lambda 捕获需要宿主函数上下文（顶层脚本暂不支持）。");
                    return new BoundErrorExpression(syntax);
                }

                if (!_environmentClasses.TryGetValue(environmentOwner, out environmentClass))
                {
                    environmentClass = new NamedTypeSymbol($"__Env_{environmentOwner.Name}", string.Empty, Visibility.Private, declaration: null)
                    {
                        BaseType = NamedTypeSymbol.SystemObject,
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
                IsLambda = true,
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

        /// <summary>
        /// Lambda return type inference (1b/B2): recursive via Compilation.BoundChildren.
        /// BoundChildren does not descend into nested lambda bodies (FunctionValueExpression
        /// only exposes its Receiver), so inner returns never leak into outer inference.
        /// </summary>
        private static TypeSymbol InferLambdaReturnType(BoundBlockStatement body, LambdaExpressionSyntax syntax)
        {
            var found = FindReturnType(body);
            return found ?? TypeSymbol.Void;

            static TypeSymbol? FindReturnType(BoundNode node)
            {
                if (node is BoundReturnStatement { Expression: { } expression } &&
                    expression.Type != TypeSymbol.Void)
                {
                    return expression.Type;
                }

                foreach (var child in Compilation.BoundChildren(node))
                {
                    var nested = FindReturnType(child);
                    if (nested != null)
                    {
                        return nested;
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// Convert all value-carrying returns in the block body to the target return type
        /// (expression bodies are handled before synthesis). 1b/B2: rewrite recursively via
        /// BoundTreeRewriter so returns in nested blocks convert too; the rewriter does not
        /// descend into nested lambda bodies, so inner lambda returns are never converted
        /// against the outer target type.
        /// </summary>
        private BoundBlockStatement ConvertLambdaBodyReturns(BoundBlockStatement body, TypeSymbol targetType, SSyntax.SyntaxNode syntax)
        {
            var converter = new LambdaReturnConverter(this, targetType);
            var converted = converter.RewriteStatement(body);
            return converted == body ? body : (BoundBlockStatement)converted;
        }

        private sealed class LambdaReturnConverter : BoundTreeRewriter
        {
            private readonly CocoaBinder _binder;
            private readonly TypeSymbol _targetType;

            public LambdaReturnConverter(CocoaBinder binder, TypeSymbol targetType)
            {
                _binder = binder;
                _targetType = targetType;
            }

            protected override BoundStatement RewriteReturnStatement(BoundReturnStatement node)
            {
                if (node.Expression is { } expression && expression.Type != _targetType)
                {
                    var converted = _binder.BindConversion(node.Syntax.Location, expression, _targetType);
                    return new BoundReturnStatement(node.Syntax, converted);
                }

                return node;
            }
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
                if (property?.Setter != null && syntax.AssignmentToken.Kind == SSyntax.SyntaxKind.EqualsToken)
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

            // facade 属性写：obj.X = v → set_X(this, v)（getter 已降级为 BoundCallExpression，接收者作首参）
            if (boundTarget is BoundCallExpression facadeGetCall &&
                facadeGetCall.Function.Name.StartsWith("get_") &&
                facadeGetCall.Function.ContainingClass is NamedTypeSymbol fc && fc.IsFacadeClass)
            {
                var propertyName = facadeGetCall.Function.Name.Substring(4);
                var property = fc.GetProperty(propertyName);
                if (property?.Setter != null && syntax.AssignmentToken.Kind == SSyntax.SyntaxKind.EqualsToken)
                {
                    if (!IsAccessibleMember(property.Setter.Visibility, property.Setter.ContainingClass!))
                    {
                        _diagnostics.ReportCannotAccessMember(syntax.AssignmentToken.Location, propertyName, property.Setter.Visibility);
                        return new BoundErrorExpression(syntax);
                    }

                    var converted = BindConversion(syntax.Expression.Location, boundExpression, property.Type);
                    return new BoundCallExpression(syntax, property.Setter, facadeGetCall.Arguments.Add(converted));
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

                if (syntax.AssignmentToken.Kind != SSyntax.SyntaxKind.EqualsToken)
                {
                    var equivalentOperatorTokenKind = SSyntax.SyntaxFacts.GetBinaryOperatorOfAssignmentOperator(syntax.AssignmentToken.Kind);
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

            if (boundTarget is BoundElementAccessExpression arrayElementTarget && syntax.AssignmentToken.Kind == SSyntax.SyntaxKind.EqualsToken)
            {
                var convertedExpression = BindConversion(syntax.Expression.Location, boundExpression, arrayElementTarget.Type);

                return new BoundElementAssignmentExpression(syntax, arrayElementTarget.Type, arrayElementTarget, convertedExpression);
            }

            // 索引器赋值：list[i] = x → set_Item（facade 经普通调用 → IL 直连 BCL；其余走 Cocoa 体）
            if (boundTarget is BoundMemberCallExpression mcIndexer && mcIndexer.Method?.ContainingProperty?.IsIndexer == true && syntax.AssignmentToken.Kind == SSyntax.SyntaxKind.EqualsToken)
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

            if (boundTarget is BoundCallExpression bcIndexer && bcIndexer.Function.ContainingProperty?.IsIndexer == true && syntax.AssignmentToken.Kind == SSyntax.SyntaxKind.EqualsToken)
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
                (syntax.AssignmentToken.Kind == SSyntax.SyntaxKind.EqualsToken ||
                 syntax.AssignmentToken.Kind == SSyntax.SyntaxKind.PlusEqualsToken ||
                 syntax.AssignmentToken.Kind == SSyntax.SyntaxKind.MinusEqualsToken))
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

        private BoundExpression BindIncrementOrDecrement(SSyntax.SyntaxNode syntax, ExpressionSyntax operandSyntax, SSyntax.SyntaxToken operatorToken)
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
                var operatorTokenKind = operatorToken.Kind == SSyntax.SyntaxKind.PlusPlusToken
                    ? SSyntax.SyntaxKind.PlusToken
                    : SSyntax.SyntaxKind.MinusToken;
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
            NamedTypeSymbol? classType;
            if (syntax.TypeArguments != null)
            {
                classType = BindGenericTypeName(syntax.Identifier, syntax.TypeArguments.Arguments) as NamedTypeSymbol;

                if (classType == null)
                {
                    return new BoundErrorExpression(syntax);
                }
            }
            else
            {
                classType = LookupType(syntax.Identifier.Text) as NamedTypeSymbol;
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

            // Constructor overload resolution (1b/B10): candidates are ALL methods whose
            // name matches the class — incl. overloads, walked along the inheritance chain.
            // Two names must be probed for instantiated generics: InstantiatedTypeSymbol.Name
            // is the mangled name (GenericTypeInstantiator.MangledName), and constructor
            // clones are deliberately renamed to it (the compiler-wide ctor lookup convention,
            // see GenericTypeInstantiator.PopulateMembers nameOverride). Legacy constructors
            // declared as `function ClassName(...)` are NOT renamed (they never got the
            // IsConstructor flag), so they keep the generic definition's simple name. The old
            // singular GetMethod(classType.Name) missed the legacy form on instantiated types
            // and skipped validation entirely (new Foo(anything) passed unchecked).
            var definitionName = classType is InstantiatedTypeSymbol instantiatedType
                ? instantiatedType.GenericDefinition.Name
                : null;
            var ctors = classType.GetMethods(classType.Name);
            if (definitionName != null && definitionName != classType.Name)
            {
                ctors = ctors.AddRange(classType.GetMethods(definitionName));
            }

            ctors = ctors.Distinct().ToImmutableArray();
            var ctor = ctors.FirstOrDefault(c => c.Parameters.Length == arguments.Count);
            if (ctor == null && (ctors.Length > 0 || arguments.Count > 0))
            {
                var arities = string.Join("/", ctors.Select(c => c.Parameters.Length).Distinct().OrderBy(x => x));
                _diagnostics.ReportError(
                    syntax.Identifier.Location,
                    arities.Length == 0
                        ? $"类 '{classType.Name}' 没有声明构造函数。"
                        : $"类 '{classType.Name}' 没有接受 {arguments.Count} 个参数的构造函数（可用元数：{arities}）。");
                return new BoundErrorExpression(syntax);
            }

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
            if (boundTarget.Type is NamedTypeSymbol cls)
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
                LookupType(staticTypeName) is NamedTypeSymbol staticType &&
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
                LookupType(enumTypeName) is NamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
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
            if (boundTarget.Type is NamedTypeSymbol classType && classType != TypeSymbol.String && !classType.IsPrimitiveValueType)
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

                    if (classType.IsFacadeClass)
                    {
                        var thisArg = BindConversion(syntax.IdentifierToken.Location, boundTarget, property.Getter.Parameters[0].Type);
                        return new BoundCallExpression(syntax, property.Getter, ImmutableArray.Create(thisArg));
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
                LookupType(staticTypeName) is NamedTypeSymbol staticType)
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

            if (boundExpression.Type is NamedTypeSymbol classType && classType != TypeSymbol.String && !classType.IsPrimitiveValueType)
            {
                if (classType.IsFacadeClass)
                {
                    var facadeMemberCall = TryBindFacadeMemberCall(syntax, identifier, boundExpression, boundArguments.ToImmutable());
                    if (facadeMemberCall != null) return facadeMemberCall;
                    var facadeObjectFace = TryBindObjectFaceMemberCall(syntax, identifier, boundExpression, boundArguments.ToImmutable());
                    if (facadeObjectFace != null) return facadeObjectFace;
                    _diagnostics.ReportUnknownMember(syntax.IdentifierToken.Location, identifier, boundExpression.Type);
                    return new BoundErrorExpression(syntax);
                }

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
        /// 6e-M19 M2-c：Object 成员面回退绑定。receiver 非 NamedTypeSymbol（基元/string/any）时查
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

            var candidates = NamedTypeSymbol.SystemObject.GetMethods(identifier)
                .Where(m => !m.IsStatic && m.BuiltinKind != null && IsAccessibleMember(m.Visibility, NamedTypeSymbol.SystemObject))
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

            // 6e-M19 M2-b：走 BoundCallExpression（顶层静态调用形状）——与 .coa 库函数消费同路径，
            // 规避 MemberCall 静态分支对含类归属符号的发射差异
            return new BoundCallExpression(syntax, method, boundArguments.ToImmutable());
        }

        /// <summary>receiver 类型 → facade 类（stdlib cod 注入；全名解析优先，cod 库直查兜底）。</summary>
        private NamedTypeSymbol? ResolveFacadeClass(TypeSymbol receiverType)
        {
            var fullName = FacadeNameOfType(receiverType);
            if (fullName == null)
            {
                return null;
            }

            // facade 类型自身（含 facade struct：其 FullName 即 BCL 全名、不在 FacadeTargets 中）
            // 直接返回自身，使其实例成员经 TryBindFacadeMemberCall 重定向到 BCL。
            if (receiverType is NamedTypeSymbol { IsFacadeClass: true } nts)
            {
                return nts;
            }

            // 全名映射表为准（cod 注入类不带序列化标记；声明侧/注入侧均已补齐，此处双保险）
            if (!FacadeTargets.ContainsKey(fullName))
            {
                return null;
            }

            if (LookupType(fullName) is NamedTypeSymbol viaLookup)
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
            if (LookupType(target) is not NamedTypeSymbol cls)
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
            if (syntax.OperatorToken.Kind == SSyntax.SyntaxKind.PlusPlusToken ||
                syntax.OperatorToken.Kind == SSyntax.SyntaxKind.MinusMinusToken)
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
        private static TypeSymbol? GetBinaryNumericResultType(TypeSymbol left, TypeSymbol right, SSyntax.SyntaxKind operatorKind)
        {
            var raw = GetRawBinaryNumericResultType(left, right, operatorKind);
            if (raw == null)
            {
                return null;
            }

            // 统一归一化：任何落在 <32 位域的整数结果升到 32 位（运算符表仅注册 32/64 位算术）
            if (raw.IsInteger && !raw.IsPlaceholder128 && raw.BitWidth < 32 &&
                operatorKind != SSyntax.SyntaxKind.ShiftLeftToken && operatorKind != SSyntax.SyntaxKind.ShiftRightToken)
            {
                return raw.IsSigned ? TypeSymbol.Int32 : TypeSymbol.UInt32;
            }

            return raw;
        }

        private static TypeSymbol? GetRawBinaryNumericResultType(TypeSymbol left, TypeSymbol right, SSyntax.SyntaxKind operatorKind)
        {
            if (operatorKind == SSyntax.SyntaxKind.ShiftLeftToken || operatorKind == SSyntax.SyntaxKind.ShiftRightToken)
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
                    NamedTypeSymbol { TypeKind: TypeKind.Delegate } dc => dc.DelegateSignature(),
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
                SSyntax.SyntaxNode firstExceedingNode;
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
            if (type is FunctionTypeSymbol expectedFunction && syntax.Kind == SSyntax.CocoaSyntaxKind.LambdaExpression)
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
            if (type is NamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateClass)
            {
                var delegateSignature = delegateClass.DelegateSignature();
                if (delegateSignature == null)
                {
                    _diagnostics.ReportError(syntax.Location, $"delegate 类 '{delegateClass.Name}' 缺少 Invoke 方法。");
                    return new BoundErrorExpression(syntax);
                }

                if (syntax.Kind == SSyntax.CocoaSyntaxKind.LambdaExpression)
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
                if (syntax.Kind == SSyntax.CocoaSyntaxKind.NameExpression)
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
            if (type is FunctionTypeSymbol functionTarget && syntax.Kind == SSyntax.CocoaSyntaxKind.NameExpression)
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
        private BoundExpression BindFunctionValueInvocation(TextLocation errorLocation, string displayName, SSyntax.SeparatedSyntaxList<ExpressionSyntax> argumentSyntaxes, BoundExpression callee, FunctionTypeSymbol functionType)
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
            if (type is NamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateTarget &&
                expression.Type == delegateTarget.DelegateSignature())
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

        private VariableSymbol BindVariableDeclaration(SSyntax.SyntaxToken identifier, bool isReadOnly, TypeSymbol type, BoundConstant? constant = null)
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

        private VariableSymbol? BindVariableReference(SSyntax.SyntaxToken identifierToken)
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

    }
}
