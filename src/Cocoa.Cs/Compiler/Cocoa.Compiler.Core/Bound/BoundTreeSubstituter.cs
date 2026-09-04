using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定树单态化替换器（6e-G7 S5）：把 `.coa` 携带的<b>开放绑定体</b>（引用泛型定义的
    /// TypeParameterSymbol/成员符号）按「定义→实例化」映射改写为封闭树。
    /// 替换面：①类型（复用 <see cref="TypeSubstituter"/>，覆盖节点内嵌 Type 字段）；
    /// ②变量（形参按序对位、局部惰性克隆）；③字段/方法/构造（泛型定义成员 → 实例化成员，按索引对齐）。
    /// </summary>
    public sealed class BoundTreeSubstituter : BoundTreeRewriter
    {
        private readonly NamedTypeSymbol _definition;
        private readonly InstantiatedTypeSymbol _instantiated;
        private readonly Dictionary<TypeParameterSymbol, TypeSymbol> _typeMap;
        private readonly Dictionary<VariableSymbol, VariableSymbol> _variableMap = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<FieldSymbol, FieldSymbol> _fieldMap = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<FunctionSymbol, FunctionSymbol> _functionMap = new(ReferenceEqualityComparer.Instance);

        private BoundTreeSubstituter(
            NamedTypeSymbol definition,
            InstantiatedTypeSymbol instantiated,
            ImmutableArray<TypeParameterSymbol> openParameters)
        {
            _definition = definition;
            _instantiated = instantiated;

            _typeMap = new Dictionary<TypeParameterSymbol, TypeSymbol>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < openParameters.Length && i < instantiated.TypeArguments.Length; i++)
            {
                _typeMap[openParameters[i]] = instantiated.TypeArguments[i];
            }

            // 成员索引对齐（GenericTypeInstantiator 物化顺序 = 定义声明顺序）
            for (var i = 0; i < definition.Fields.Length && i < instantiated.Fields.Length; i++)
            {
                _fieldMap[definition.Fields[i]] = instantiated.Fields[i];
            }

            // 6e 跨库里程碑：方法映射按「名 + 元数」匹配而非索引——实例化类的 Methods 顺序
            // （Populate：先普通方法后属性访问器）与 cod 读侧 fn 条目序可能不同，索引错配会
            // 令替换后 body 内 membercall 解析到错误实例化方法（Dictionary HashCode 等 KeyNotFound）。
            foreach (var definitionMethod in definition.Methods)
            {
                FunctionSymbol? match = null;
                foreach (var instantiatedMethod in instantiated.Methods)
                {
                    if (definitionMethod.Name == instantiatedMethod.Name &&
                        definitionMethod.Parameters.Length == instantiatedMethod.Parameters.Length)
                    {
                        match = instantiatedMethod;
                        break;
                    }
                }

                if (match != null)
                {
                    _functionMap[definitionMethod] = match;
                }
            }

            // 6e 跨库里程碑：属性访问器（get_X/set_X）不入 instantiated.Methods（Populate 仅经属性循环
            // 创建），故须按属性索引把定义访问器 → 实例化访问器补入 _functionMap，否则替换后 body 内
            // `this[k] = v`（set_Item）等 membercall 仍指向定义方法 → Evaluator _functions KeyNotFound。
            for (var i = 0; i < definition.Properties.Length && i < instantiated.Properties.Length; i++)
            {
                var definitionProperty = definition.Properties[i];
                var instantiatedProperty = instantiated.Properties[i];

                if (definitionProperty.Getter != null && instantiatedProperty.Getter != null)
                {
                    _functionMap[definitionProperty.Getter] = instantiatedProperty.Getter;
                }

                if (definitionProperty.Setter != null && instantiatedProperty.Setter != null)
                {
                    _functionMap[definitionProperty.Setter] = instantiatedProperty.Setter;
                }
            }
        }

        public static BoundBlockStatement SubstituteMethodBody(
            BoundBlockStatement openBody,
            NamedTypeSymbol definition,
            InstantiatedTypeSymbol instantiated,
            FunctionSymbol instantiatedMethod,
            FunctionSymbol? definitionMethodOverride = null)
        {
            var substituter = new BoundTreeSubstituter(definition, instantiated, definition.TypeParameters);

            var definitionMethod = definitionMethodOverride ?? FindDefinitionMethod(definition, instantiatedMethod);
            if (definitionMethod != null)
            {
                for (var i = 0; i < definitionMethod.Parameters.Length &&
                                i < instantiatedMethod.Parameters.Length; i++)
                {
                    substituter._variableMap[definitionMethod.Parameters[i]] = instantiatedMethod.Parameters[i];
                }
            }

            return (BoundBlockStatement)substituter.RewriteStatement(openBody);
        }

        private static FunctionSymbol? FindDefinitionMethod(NamedTypeSymbol definition, FunctionSymbol instantiatedMethod)
        {
            // 实例化构造器被 Populate 改名为实例化类 mangle 名——按构造器身份优先匹配；
            // 其余按名字匹配（开放体 v1 无重载场景，名字唯一）
            foreach (var candidate in definition.Methods)
            {
                if (instantiatedMethod.IsConstructor && candidate.IsConstructor)
                {
                    return candidate;
                }
            }

            foreach (var candidate in definition.Methods)
            {
                if (!candidate.IsConstructor && candidate.Name == instantiatedMethod.Name)
                {
                    return candidate;
                }
            }

            return null;
        }

        private TypeSymbol SubstituteType(TypeSymbol type) => TypeSubstituter.Substitute(type, _typeMap);

        private VariableSymbol MapVariable(VariableSymbol variable)
        {
            if (_variableMap.TryGetValue(variable, out var mapped))
            {
                return mapped;
            }

            if (variable is GlobalVariableSymbol || variable.IsCaptured)
            {
                return variable;
            }

            var clone = new LocalVariableSymbol(variable.Name, variable.IsReadOnly, SubstituteType(variable.Type), null);
            _variableMap[variable] = clone;
            return clone;
        }

        private FieldSymbol? MapField(FieldSymbol? field)
        {
            if (field == null)
            {
                return null;
            }

            return _fieldMap.TryGetValue(field, out var mapped) ? mapped : field;
        }

        private FunctionSymbol MapFunction(FunctionSymbol function)
        {
            return _functionMap.TryGetValue(function, out var mapped) ? mapped : function;
        }

        protected override BoundExpression RewriteVariableExpression(BoundVariableExpression node)
        {
            var variable = MapVariable(node.Variable);
            return variable == node.Variable
                ? node
                : new BoundVariableExpression(node.Syntax, variable);
        }

        protected override BoundStatement RewriteVariableDeclaration(BoundVariableDeclaration node)
        {
            var initializer = RewriteExpression(node.Initializer);
            var variable = MapVariable(node.Variable);
            if (initializer == node.Initializer && variable == node.Variable)
            {
                return node;
            }

            return new BoundVariableDeclaration(node.Syntax, variable, initializer);
        }

        protected override BoundExpression RewriteAssignmentExpression(BoundAssignmentExpression node)
        {
            var expression = RewriteExpression(node.Expression);
            var variable = MapVariable(node.Variable);
            if (expression == node.Expression && variable == node.Variable)
            {
                return node;
            }

            return new BoundAssignmentExpression(node.Syntax, variable, expression);
        }

        protected override BoundExpression RewriteCompoundAssignmentExpression(BoundCompoundAssignmentExpression node)
        {
            var expression = RewriteExpression(node.Expression);
            var variable = MapVariable(node.Variable);
            if (expression == node.Expression && variable == node.Variable)
            {
                return node;
            }

            return new BoundCompoundAssignmentExpression(node.Syntax, variable, node.Op, expression);
        }

        protected override BoundExpression RewriteMemberAccessExpression(BoundMemberAccessExpression node)
        {
            var target = RewriteExpression(node.Target);
            var field = MapField(node.Field);
            if (target == node.Target && field == node.Field)
            {
                return node;
            }

            var type = field != null ? SubstituteType(field.Type) : node.Type;
            return new BoundMemberAccessExpression(node.Syntax, type, target, node.Identifier, field);
        }

        protected override BoundExpression RewriteMemberCallExpression(BoundMemberCallExpression node)
        {
            var expression = RewriteExpression(node.Expression);
            var method = node.Method;

            // 6e 跨库里程碑：membercall 的接收者可能是另一泛型实例化的字段/局部（如 ListEnumerator.MoveNext
            // 内 `_list.Count`，_list: List<ListEnumerator.T>）——其方法的 ContainingClass 为 List<T>
            // 定义，≠ 当前替换的定义类。按「替换后接收者类型 + 方法名/元数」在实例化类型上重定位，
            // 保证 IL _methods / Evaluator _functions 命中实例化方法（而非定义方法）。
            if (method != null)
            {
                if (method.ContainingClass == _definition)
                {
                    method = MapFunction(method);
                }
                else if (expression.Type is NamedTypeSymbol receiverType && receiverType != _definition)
                {
                    var candidate = receiverType.GetMethods(method.Name)
                        .FirstOrDefault(m => m.Parameters.Length == method.Parameters.Length);

                    // 访问器（get_X/set_X）不在 Methods 而在 Properties；GetProperty 会触发物化。
                    if (candidate == null &&
                        (method.Name.StartsWith("get_", StringComparison.Ordinal) ||
                         method.Name.StartsWith("set_", StringComparison.Ordinal)))
                    {
                        var propertyName = method.Name.Substring(4);
                        var property = receiverType.GetProperty(propertyName);
                        var accessor = method.Name.StartsWith("get_", StringComparison.Ordinal)
                            ? property?.Getter
                            : property?.Setter;
                        if (accessor != null && accessor.Parameters.Length == method.Parameters.Length)
                        {
                            candidate = accessor;
                        }
                    }

                    if (candidate != null)
                    {
                        method = candidate;
                    }
                }
            }

            var builder = ImmutableArray.CreateBuilder<BoundExpression>(node.Arguments.Length);
            foreach (var argument in node.Arguments)
            {
                builder.Add(RewriteExpression(argument));
            }

            var type = SubstituteType(node.Type);
            return new BoundMemberCallExpression(node.Syntax, expression, node.Identifier, builder.ToImmutable(), type, method, node.IsBase);
        }

        protected override BoundExpression RewriteMemberAssignmentExpression(BoundMemberAssignmentExpression node)
        {
            var target = RewriteExpression(node.Target);
            var field = MapField(node.Field);
            var expression = RewriteExpression(node.Expression);
            if (target == node.Target && field == node.Field && expression == node.Expression)
            {
                return node;
            }

            return new BoundMemberAssignmentExpression(node.Syntax, target, field!, expression);
        }

        protected override BoundExpression RewriteCallExpression(BoundCallExpression node)
        {
            var function = MapFunction(node.Function);

            var builder = ImmutableArray.CreateBuilder<BoundExpression>(node.Arguments.Length);
            foreach (var argument in node.Arguments)
            {
                builder.Add(RewriteExpression(argument));
            }

            return new BoundCallExpression(node.Syntax, function, builder.ToImmutable());
        }

        protected override BoundExpression RewriteObjectCreationExpression(BoundObjectCreationExpression node)
        {
            var arguments = ImmutableArray.CreateBuilder<BoundExpression>(node.Arguments.Length);
            foreach (var argument in node.Arguments)
            {
                arguments.Add(RewriteExpression(argument));
            }

            return new BoundObjectCreationExpression(node.Syntax, (NamedTypeSymbol)SubstituteType(node.Type), arguments.ToImmutable());
        }

        protected override BoundExpression RewriteConversionExpression(BoundConversionExpression node)
        {
            var type = SubstituteType(node.Type);
            var expression = RewriteExpression(node.Expression);
            if (type == node.Type && expression == node.Expression)
            {
                return node;
            }

            return new BoundConversionExpression(node.Syntax, type, expression);
        }

        protected override BoundExpression RewriteArrayCreationExpression(BoundArrayCreationExpression node)
        {
            var length = RewriteExpression(node.Length);
            var builder = ImmutableArray.CreateBuilder<BoundExpression>(node.Initializers.Length);
            foreach (var initializer in node.Initializers)
            {
                builder.Add(RewriteExpression(initializer));
            }

            var initializers = builder.ToImmutable();
            var type = SubstituteType(node.Type);
            if (length == node.Length && initializers == node.Initializers && type == node.Type)
            {
                return node;
            }

            return new BoundArrayCreationExpression(node.Syntax, type, length, initializers);
        }

        protected override BoundExpression RewriteElementAccessExpression(BoundElementAccessExpression node)
        {
            var target = RewriteExpression(node.Target);
            var index = RewriteExpression(node.Index);
            var type = SubstituteType(node.Type);
            if (target == node.Target && index == node.Index && type == node.Type)
            {
                return node;
            }

            return new BoundElementAccessExpression(node.Syntax, type, target, index);
        }

        protected override BoundExpression RewriteElementAssignmentExpression(BoundElementAssignmentExpression node)
        {
            var targetType = SubstituteType(node.Type);
            var target = (BoundElementAccessExpression)RewriteExpression(node.Target);
            var value = RewriteExpression(node.Expression);
            if (target == node.Target && value == node.Expression && targetType == node.Type)
            {
                return node;
            }

            return new BoundElementAssignmentExpression(node.Syntax, targetType, target, value);
        }

        protected override BoundExpression RewriteThisExpression(BoundThisExpression node)
        {
            var type = SubstituteType(node.Type);
            if (type == node.Type)
            {
                return node;
            }

            return new BoundThisExpression(node.Syntax, (NamedTypeSymbol)type);
        }

        protected override BoundExpression RewriteIsExpression(BoundIsExpression node)
        {
            var expression = RewriteExpression(node.Expression);
            var targetType = SubstituteType(node.TargetType);
            if (expression == node.Expression && targetType == node.TargetType)
            {
                return node;
            }

            return new BoundIsExpression(node.Syntax, expression, targetType);
        }

        protected override BoundExpression RewriteAsExpression(BoundAsExpression node)
        {
            var expression = RewriteExpression(node.Expression);
            var targetType = SubstituteType(node.TargetType);
            if (expression == node.Expression && targetType == node.TargetType)
            {
                return node;
            }

            return new BoundAsExpression(node.Syntax, expression, targetType);
        }
    }
}
