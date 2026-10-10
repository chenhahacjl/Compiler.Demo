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
    /// 类型测试 / 模式匹配表达式绑定（自 Statements 拆出：is/as/cast/模式/条件访问/typeof）。
    /// </summary>
    public partial class CocoaBinder
    {
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

        /// <summary>
        /// is 类型测试 / 模式匹配。各模式产专用模式节点（<c>BoundRelationalPattern</c> 等），
        /// 由**各后端**自行求值：Evaluator 已完整实现；IL 后端的实现见 <c>IlEmitter.EmitPattern*</c>。
        /// 不在绑定期降级为普通布尔表达式——那会丢掉 `any` 接收者的动态类型测试/动态成员查找语义。
        /// </summary>
        private BoundExpression BindIsExpression(IsExpressionSyntax syntax)
        {
            var operand = BindExpression(syntax.Expression);
            if (operand.Type == TypeSymbol.Error)
                return new BoundErrorExpression(syntax);

            return BindIsPatternCore(syntax, operand);
        }

        private BoundExpression BindIsPatternCore(IsExpressionSyntax syntax, BoundExpression operand)
        {
            // 常量模式：expr is null → expr == null / expr is 0 → expr == 0
            if (syntax.Pattern is CoreSyntax.ConstantPatternSyntax constantPattern)
            {
                var patternValue = BindExpression((CoreSyntax.ExpressionSyntax)constantPattern.Expression);
                if (patternValue.Type == TypeSymbol.Error)
                    return new BoundErrorExpression(syntax);

                return BoundNodeFactory.Binary(syntax, operand, CoreSyntax.SyntaxKind.EqualsEqualsToken, patternValue);
            }

            // 声明模式：expr is A a → 类型测试 + 变量绑定
            if (syntax.Pattern is CoreSyntax.DeclarationPatternSyntax declarationPattern)
            {
                var type = LookupType(declarationPattern.TypeToken.Text ?? "?");
                if (type == null)
                {
                    _diagnostics.ReportUndefinedType(declarationPattern.TypeToken.Location, declarationPattern.TypeToken.Text ?? "?");
                    return new BoundErrorExpression(syntax);
                }

                var variable = new LocalVariableSymbol(declarationPattern.VariableToken.Text ?? "_", isReadOnly: false, type, constant: null);
                return new BoundDeclarationPattern(syntax, operand, type, variable);
            }

            // 关系模式：expr is > 0 / expr is <= 10
            if (syntax.Pattern is CoreSyntax.RelationalPatternSyntax relationalPattern)
            {
                var value = BindExpression((CoreSyntax.ExpressionSyntax)relationalPattern.Value);
                if (value.Type == TypeSymbol.Error)
                    return new BoundErrorExpression(syntax);

                var opKind = relationalPattern.OperatorToken.Kind switch
                {
                    CoreSyntax.SyntaxKind.GreaterToken => BoundBinaryOperatorKind.Greater,
                    CoreSyntax.SyntaxKind.GreaterOrEqualsToken => BoundBinaryOperatorKind.GreaterOrEquals,
                    CoreSyntax.SyntaxKind.LessToken => BoundBinaryOperatorKind.Less,
                    CoreSyntax.SyntaxKind.LessOrEqualsToken => BoundBinaryOperatorKind.LessOrEquals,
                    _ => BoundBinaryOperatorKind.Greater
                };

                return new BoundRelationalPattern(syntax, operand, opKind, value);
            }

            // 逻辑模式：expr is > 0 and < 10 / expr is not null
            if (syntax.Pattern is CoreSyntax.LogicalPatternSyntax logicalPattern)
            {
                return BindLogicalPattern(syntax, operand, logicalPattern);
            }

            // 属性模式：expr is { Length: > 0 }
            if (syntax.Pattern is CoreSyntax.PropertyPatternSyntax propertyPattern)
            {
                return BindPropertyPattern(syntax, operand, propertyPattern);
            }

            // when 子句模式：expr is <pattern> when <cond>
            if (syntax.Pattern is CoreSyntax.WhenPatternSyntax whenPattern)
            {
                return BindWhenPattern(syntax, operand, whenPattern);
            }

            // 回退：类型测试（旧路径）
            if (syntax.TypeName != null)
            {
                return BindTypeTestOrAs(syntax.Expression, syntax.TypeName, syntax, wantBool: true);
            }

            return new BoundErrorExpression(syntax);
        }

        private BoundExpression BindLogicalPattern(CoreSyntax.SyntaxNode syntax, BoundExpression operand, CoreSyntax.LogicalPatternSyntax logicalPattern)
        {
            if (logicalPattern.IsUnary)
            {
                var innerPattern = BindPattern(operand, logicalPattern.Pattern!);
                return new BoundLogicalPattern(syntax, BoundLogicalPatternKind.Not, innerPattern);
            }
            else
            {
                var leftPattern = BindPattern(operand, logicalPattern.Left!);
                var rightPattern = BindPattern(operand, logicalPattern.Right!);
                var opKind = logicalPattern.OperatorToken!.Kind == CoreSyntax.SyntaxKind.AndKeyword
                    ? BoundLogicalPatternKind.And
                    : BoundLogicalPatternKind.Or;
                return new BoundLogicalPattern(syntax, leftPattern, opKind, rightPattern);
            }
        }

        /// <summary>
        /// when 子句降级：<c>e is &lt;pattern&gt; when &lt;cond&gt;</c> ≡ <c>&lt;pattern&gt; &amp;&amp; cond</c>。
        /// 条件对已通过内层类型测试的匹配值求值，故声明模式变量在条件里可直接按名引用
        /// （变量由 <see cref="DeclarePatternVariables"/> 先行声明）。
        /// </summary>
        private BoundExpression BindWhenPattern(
            CoreSyntax.SyntaxNode syntax,
            BoundExpression operand,
            CoreSyntax.WhenPatternSyntax whenPattern)
        {
            var inner = BindPattern(operand, whenPattern.Pattern);
            if (inner.Type == TypeSymbol.Error)
            {
                return new BoundErrorExpression(whenPattern);
            }

            DeclarePatternVariables(inner);

            var condition = BindExpression(whenPattern.Condition, TypeSymbol.Boolean);
            if (condition.Type == TypeSymbol.Error)
            {
                return new BoundErrorExpression(whenPattern);
            }

            return new BoundLogicalPattern(whenPattern, inner, BoundLogicalPatternKind.And, condition);
        }

        /// <summary>
        /// 属性模式 <c>e is { X: &gt; 0 }</c>：产出 <see cref="BoundPropertyPattern"/>，
        /// 成员解析（字段/属性/动态成员）由各后端按接收者静态类型或运行时类型处理。
        /// </summary>
        private BoundExpression BindPropertyPattern(CoreSyntax.SyntaxNode syntax, BoundExpression operand, CoreSyntax.PropertyPatternSyntax propertyPattern)
        {
            var subpatterns = ImmutableArray.CreateBuilder<BoundPropertySubpattern>();

            foreach (var sub in propertyPattern.Subpatterns)
            {
                // 常量子模式直接绑值（不经 BindPattern，后者会造比较表达式）
                if (sub.Pattern is CoreSyntax.ConstantPatternSyntax constantSub)
                {
                    var patternValue = BindExpression((CoreSyntax.ExpressionSyntax)constantSub.Expression);
                    subpatterns.Add(new BoundPropertySubpattern(sub.NameToken.Text ?? "", patternValue));
                }
                else
                {
                    var subPattern = BindPattern(operand, sub.Pattern);
                    subpatterns.Add(new BoundPropertySubpattern(sub.NameToken.Text ?? "", subPattern));
                }
            }

            return new BoundPropertyPattern(propertyPattern, operand, subpatterns.ToImmutable());
        }

        private BoundExpression BindPattern(BoundExpression operand, CoreSyntax.PatternSyntax pattern)
        {
            if (pattern is CoreSyntax.ConstantPatternSyntax constantPattern)
            {
                var patternValue = BindExpression((CoreSyntax.ExpressionSyntax)constantPattern.Expression);
                return BoundNodeFactory.Binary(pattern, operand, CoreSyntax.SyntaxKind.EqualsEqualsToken, patternValue);
            }

            if (pattern is CoreSyntax.DeclarationPatternSyntax declarationPattern)
            {
                var type = LookupType(declarationPattern.TypeToken.Text ?? "?");
                if (type == null)
                {
                    _diagnostics.ReportUndefinedType(declarationPattern.TypeToken.Location, declarationPattern.TypeToken.Text ?? "?");
                    return new BoundErrorExpression(pattern);
                }

                var variable = new LocalVariableSymbol(declarationPattern.VariableToken.Text ?? "_", isReadOnly: false, type, constant: null);
                return new BoundDeclarationPattern(pattern, operand, type, variable);
            }

            if (pattern is CoreSyntax.RelationalPatternSyntax relationalPattern)
            {
                var value = BindExpression((CoreSyntax.ExpressionSyntax)relationalPattern.Value);
                var opKind = relationalPattern.OperatorToken.Kind switch
                {
                    CoreSyntax.SyntaxKind.GreaterToken => BoundBinaryOperatorKind.Greater,
                    CoreSyntax.SyntaxKind.GreaterOrEqualsToken => BoundBinaryOperatorKind.GreaterOrEquals,
                    CoreSyntax.SyntaxKind.LessToken => BoundBinaryOperatorKind.Less,
                    CoreSyntax.SyntaxKind.LessOrEqualsToken => BoundBinaryOperatorKind.LessOrEquals,
                    _ => BoundBinaryOperatorKind.Greater
                };
                return new BoundRelationalPattern(pattern, operand, opKind, value);
            }

            if (pattern is CoreSyntax.LogicalPatternSyntax logicalPattern)
            {
                return BindLogicalPattern(pattern, operand, logicalPattern);
            }

            if (pattern is CoreSyntax.PropertyPatternSyntax propertyPattern)
            {
                return BindPropertyPattern(pattern, operand, propertyPattern);
            }

            if (pattern is CoreSyntax.WhenPatternSyntax whenPattern)
            {
                return BindWhenPattern(pattern, operand, whenPattern);
            }

            return new BoundErrorExpression(pattern);
        }

        /// <summary>6e-M19 M5-b：as 类型转换——同 is 的静态判定；动态情形失败得 null。</summary>
        private BoundExpression BindAsExpression(AsExpressionSyntax syntax)
        {
            return BindTypeTestOrAs(syntax.Expression, syntax.TypeName, syntax, wantBool: false);
        }

        private BoundExpression BindNameofExpression(NameofExpressionSyntax syntax)
        {
            var name = ExtractName(syntax.Argument);
            return new BoundLiteralExpression(syntax, name, TypeSymbol.String);
        }

        /// <summary>
        /// <c>typeof(T)</c> → <see cref="NamedTypeSymbol.SystemType"/>；
        /// <c>sizeof(T)</c> → 字节数（基元类型编译期折叠为常量，C# 同；其余类型交发射层发 <c>sizeof T</c>）。
        /// </summary>
        private BoundExpression BindTypeOperatorExpression(TypeOperatorExpressionSyntax syntax)
        {
            var type = BindTypeClause(syntax.Type);
            if (type == null || type == TypeSymbol.Error)
            {
                return new BoundErrorExpression(syntax);
            }

            if (syntax.IsTypeOf)
            {
                return new BoundTypeOperatorExpression(syntax, isTypeOf: true, type, NamedTypeSymbol.SystemType);
            }

            // sizeof：基元按宽度折叠为编译期常量（C# sizeof(int) 即编译期常量）。
            // bool/char 不在 TypeSymbol.BitWidth 的枚举里（该属性只覆盖 8/16/32/64/128 位数值型），故单列。
            var primitiveSize = type == TypeSymbol.Boolean ? 1
                : type == TypeSymbol.Char ? 2
                : type.BitWidth > 0 ? type.BitWidth / 8
                : 0;

            if (primitiveSize > 0)
            {
                return new BoundLiteralExpression(syntax, primitiveSize, TypeSymbol.Int32);
            }

            if (type is NamedTypeSymbol { TypeKind: TypeKind.Enum })
            {
                // 枚举底层固定为 i32（NamedTypeSymbol 无显式底层类型字段）
                return new BoundLiteralExpression(syntax, 4, TypeSymbol.Int32);
            }

            return new BoundTypeOperatorExpression(syntax, isTypeOf: false, type, TypeSymbol.Int32);
        }

        private static string ExtractName(ExpressionSyntax expression)
        {
            return expression switch
            {
                NameExpressionSyntax nameExpr => nameExpr.IdentifierToken.Text ?? "",
                MemberAccessExpressionSyntax memberExpr => memberExpr.IdentifierToken.Text ?? "",
                _ => expression.ToString(),
            };
        }

        private BoundExpression BindConditionalAccessExpression(ConditionalAccessExpressionSyntax syntax)
        {
            var expression = BindExpression(syntax.Expression);

            BoundExpression whenNotNull;
            if (syntax.WhenNotNull is NameExpressionSyntax nameExpr)
            {
                whenNotNull = BindMemberAccessOnExpression(expression, nameExpr.IdentifierToken.Text, nameExpr);
            }
            else
            {
                whenNotNull = BindExpression(syntax.WhenNotNull);
            }

            return new BoundConditionalAccessExpression(syntax, expression, whenNotNull);
        }

        private BoundExpression BindMemberAccessOnExpression(BoundExpression instance, string memberName, CoreSyntax.SyntaxNode syntax)
        {
            if (instance.Type == TypeSymbol.Error)
                return new BoundErrorExpression(syntax);

            if (instance.Type == TypeSymbol.String && memberName == "Length")
            {
                return new BoundMemberAccessExpression(syntax, TypeSymbol.Int32, instance, memberName, null);
            }

            if (instance.Type is NamedTypeSymbol classType && classType != TypeSymbol.String && !classType.IsPrimitiveValueType)
            {
                var field = classType.GetField(memberName);
                if (field != null)
                    return new BoundMemberAccessExpression(syntax, field.Type, instance, memberName, field);
            }

            _diagnostics.ReportCannotAccessMember(syntax.Location, memberName, Visibility.Private);
            return new BoundErrorExpression(syntax);
        }

        private BoundExpression BindTypeTestOrAs(ExpressionSyntax expressionSyntax, CoreSyntax.SyntaxToken typeName, ExpressionSyntax ownerSyntax, bool wantBool)
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

            // 目标约束：非接口类或 string（接口 is/as 判定三后端一致先拒；接口方法分派 B3 已支持、数组无类型对象）
            var targetClass = target as NamedTypeSymbol;

            // `is/as String` 解析为 System.String 承载类（facade/外部）→ 归一为基元 string
            if (targetClass != null && (targetClass.FullName == "System.String" || targetClass.FullName == "string"))
            {
                target = TypeSymbol.String;
                targetClass = null;
            }

            if ((targetClass != null && targetClass.IsInterface) || target.ElementType != null ||
                (targetClass == null && target != TypeSymbol.String && !target.IsPrimitiveValueType))
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

            // object 多态：值类型 is/as 目标（obj is int / obj as double / obj is string）——运行时对装箱值做类型判定。
            // 置于类接收者逻辑之前（基元/String 目标无 targetClass，避免 targetClass! 空引用）。
            if (target.IsPrimitiveValueType || target == TypeSymbol.String)
            {
                return wantBool
                    ? new BoundIsExpression(ownerSyntax, operand, target)
                    : new BoundAsExpression(ownerSyntax, operand, target);
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
    }
}
