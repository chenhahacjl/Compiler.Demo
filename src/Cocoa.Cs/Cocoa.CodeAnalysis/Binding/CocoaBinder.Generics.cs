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
namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 泛型方法解析与类型子句绑定（自 Statements 拆出，6e-M22 泛型族）。
    /// </summary>
    public partial class CocoaBinder
    {
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
        /// 内建委托家族解析（6e-M22 C3，设计 §2.3）：编译器预合成、零 stdlib 依赖。
        /// `Func&lt;A1..An,R&gt;` = (A..) -&gt; R（1~16 参）；`Action&lt;A1..An&gt;` = (A..) -&gt; void（0~16 参）；
        /// `Predicate&lt;T&gt;` = (T) -&gt; bool。非家族名返回 null 回落常规查找；命中但元数/绑定失败报诊断返回 Error 壳。
        /// </summary>
        private TypeSymbol? TryResolveDelegateFamily(CoreSyntax.SyntaxToken identifier, ImmutableArray<TypeClauseSyntax> argumentClauses)
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
        public TypeSymbol? BindGenericTypeNameForExpansion(CoreSyntax.SyntaxToken identifier, ImmutableArray<CoreSyntax.SyntaxNode> argumentClauses)
            => BindGenericTypeName(identifier, argumentClauses.Cast<TypeClauseSyntax>().ToImmutableArray());

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
        private TypeSymbol? BindGenericTypeName(CoreSyntax.SyntaxToken identifier, ImmutableArray<TypeClauseSyntax> argumentClauses)
        {
            // 内建委托家族（6e-M22 C3）：Func<…>/Action<…>/Predicate<T> → 结构化函数类型
            var familyResult = TryResolveDelegateFamily(identifier, argumentClauses);
            if (familyResult != null)
            {
                return familyResult;
            }

            // N1：同名不同元数的泛型类型（如 ValueTuple<T1>..<T1..T7>）——按实参个数精确查找，未命中回退原名
            var definition = (LookupType(identifier.Text, argumentClauses.Length) ?? LookupType(identifier.Text)) as NamedTypeSymbol;
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

            return InstantiateGenericMethodCore(errorLocation, displayName, definition, arguments.ToImmutable(), argumentCount);
        }

        /// <summary>类型实参已解析为符号的实例化核心（显式实参与类型推断共用）。</summary>
        private FunctionSymbol? InstantiateGenericMethodCore(TextLocation errorLocation, string displayName, FunctionSymbol definition, ImmutableArray<TypeSymbol> arguments, int argumentCount)
        {
            ValidateTypeArgumentConstraints(errorLocation, definition.TypeParameters, arguments, displayName);

            var instantiated = GenericMethodInstantiator.Instantiate(definition, arguments);

            if (instantiated.Parameters.Length != argumentCount)
            {
                _diagnostics.ReportWrongArgumentCount(errorLocation, displayName, instantiated.Parameters.Length, argumentCount);
                return null;
            }

            return instantiated;
        }

        /// <summary>泛型方法类型推断（阶段 1，对齐 C#）：从实参类型反推类型参数，支持嵌套泛型（List&lt;T&gt;、T[]）。</summary>
        private bool TryInferTypeArguments(FunctionSymbol definition, ImmutableArray<BoundExpression> arguments, out ImmutableArray<TypeSymbol> inferred)
        {
            inferred = default;
            var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
            var count = Math.Min(definition.Parameters.Length, arguments.Length);
            for (var i = 0; i < count; i++)
            {
                if (definition.Parameters[i].IsByRef)
                {
                    continue; // 泛型 byref 参数不参与推断（保持显式实参）
                }

                if (!UnifyParameter(definition.Parameters[i].Type, arguments[i].Type, map))
                {
                    return false;
                }
            }

            var result = ImmutableArray.CreateBuilder<TypeSymbol>(definition.TypeParameters.Length);
            foreach (var typeParameter in definition.TypeParameters)
            {
                if (!map.TryGetValue(typeParameter, out var bound))
                {
                    return false; // 未能推断出该类型参数
                }

                result.Add(bound);
            }

            inferred = result.ToImmutable();
            return true;
        }

        /// <summary>类型统一：形参声明类型 ↔ 实参类型 → 类型参数映射（含 T[] / X&lt;T&gt; 嵌套反推；非泛型情形不约束）。</summary>
        private bool UnifyParameter(TypeSymbol parameterType, TypeSymbol argumentType, Dictionary<TypeParameterSymbol, TypeSymbol> map)
        {
            if (parameterType is TypeParameterSymbol typeParameter)
            {
                if (map.TryGetValue(typeParameter, out var existing))
                {
                    return existing == argumentType;
                }

                map[typeParameter] = argumentType;
                return true;
            }

            if (parameterType is ArrayTypeSymbol parameterArray && argumentType is ArrayTypeSymbol argumentArray)
            {
                return UnifyParameter(parameterArray.ElementType!, argumentArray.ElementType!, map);
            }

            if (parameterType is NamedTypeSymbol parameterNamed && argumentType is NamedTypeSymbol argumentNamed)
            {
                var parameterDefinition = parameterNamed is InstantiatedTypeSymbol parameterInstantiated ? parameterInstantiated.GenericDefinition : parameterNamed;
                var argumentDefinition = argumentNamed is InstantiatedTypeSymbol argumentInstantiated ? argumentInstantiated.GenericDefinition : argumentNamed;
                if (parameterDefinition == argumentDefinition)
                {
                    var parameterArguments = parameterNamed is InstantiatedTypeSymbol parameterArgs
                        ? parameterArgs.TypeArguments
                        : parameterNamed.TypeParameters.Select(t => (TypeSymbol)t).ToImmutableArray();
                    var argumentArguments = argumentNamed is InstantiatedTypeSymbol argumentArgs
                        ? argumentArgs.TypeArguments
                        : argumentNamed.TypeParameters.Select(t => (TypeSymbol)t).ToImmutableArray();
                    if (parameterArguments.Length == argumentArguments.Length)
                    {
                        for (var i = 0; i < parameterArguments.Length; i++)
                        {
                            if (!UnifyParameter(parameterArguments[i], argumentArguments[i], map))
                            {
                                return false;
                            }
                        }

                        return true;
                    }
                }
            }

            return true;
        }

        /// <summary>泛型方法实参转换绑定（元数已由共享核心校验）。</summary>
        private ImmutableArray<BoundExpression> BindGenericMethodArguments(CoreSyntax.SeparatedSyntaxList<ExpressionSyntax> argumentSyntaxes, FunctionSymbol instantiated)
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

    }
}
