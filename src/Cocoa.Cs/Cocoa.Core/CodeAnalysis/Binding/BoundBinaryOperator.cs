using System.Collections.Generic;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定二元操作符
    /// </summary>
    internal sealed class BoundBinaryOperator
    {
        private BoundBinaryOperator(SyntaxKind syntaxKind, BoundBinaryOperatorKind kind, TypeSymbol type)
            : this(syntaxKind, kind, type, type, type)
        {
        }

        private BoundBinaryOperator(SyntaxKind syntaxKind, BoundBinaryOperatorKind kind, TypeSymbol operandType, TypeSymbol resultType)
            : this(syntaxKind, kind, operandType, operandType, resultType)
        {
        }

        private BoundBinaryOperator(SyntaxKind syntaxKind, BoundBinaryOperatorKind kind, TypeSymbol leftType, TypeSymbol rightType, TypeSymbol resultType)
        {
            SyntaxKind = syntaxKind;
            Kind = kind;
            LeftType = leftType;
            RightType = rightType;
            ResultType = resultType;
        }

        public SyntaxKind SyntaxKind { get; }
        public BoundBinaryOperatorKind Kind { get; }
        public TypeSymbol LeftType { get; }
        public TypeSymbol RightType { get; }
        public TypeSymbol ResultType { get; }

        private static readonly BoundBinaryOperator[] _operators = BuildOperators();

        /// <summary>
        /// 6e-M21 Phase 1：程序化生成运算符表——10 个数值类型（i8/i16/i32/i64/u8/u16/u32/u64/f32/f64）
        /// 各一份完整集合；混合精度由 Binder 的二元提升先归一到公共类型再查表。
        /// </summary>
        private static BoundBinaryOperator[] BuildOperators()
        {
            var ops = new List<BoundBinaryOperator>
            {
                // bool：逻辑/位/相等
                new BoundBinaryOperator(SyntaxKind.AmpersandToken, BoundBinaryOperatorKind.BitwiseAnd, TypeSymbol.Boolean),
                new BoundBinaryOperator(SyntaxKind.AmpersandAmpersandToken, BoundBinaryOperatorKind.LogicalAnd, TypeSymbol.Boolean),
                new BoundBinaryOperator(SyntaxKind.PipeToken, BoundBinaryOperatorKind.BitwiseOr, TypeSymbol.Boolean),
                new BoundBinaryOperator(SyntaxKind.PipePipeToken, BoundBinaryOperatorKind.LogicalOr, TypeSymbol.Boolean),
                new BoundBinaryOperator(SyntaxKind.HatToken, BoundBinaryOperatorKind.BitwiseXor, TypeSymbol.Boolean),
                new BoundBinaryOperator(SyntaxKind.EqualsEqualsToken, BoundBinaryOperatorKind.Equals, TypeSymbol.Boolean, TypeSymbol.Boolean),
                new BoundBinaryOperator(SyntaxKind.BangEqualsToken, BoundBinaryOperatorKind.NotEquals, TypeSymbol.Boolean, TypeSymbol.Boolean),

                // string：拼接与相等
                new BoundBinaryOperator(SyntaxKind.PlusToken, BoundBinaryOperatorKind.Addition, TypeSymbol.String),
                new BoundBinaryOperator(SyntaxKind.PlusToken, BoundBinaryOperatorKind.Addition, TypeSymbol.String, TypeSymbol.Double, TypeSymbol.String),
                new BoundBinaryOperator(SyntaxKind.EqualsEqualsToken, BoundBinaryOperatorKind.Equals, TypeSymbol.String, TypeSymbol.Boolean),
                new BoundBinaryOperator(SyntaxKind.BangEqualsToken, BoundBinaryOperatorKind.NotEquals, TypeSymbol.String, TypeSymbol.Boolean),

                // char：相等
                new BoundBinaryOperator(SyntaxKind.EqualsEqualsToken, BoundBinaryOperatorKind.Equals, TypeSymbol.Char, TypeSymbol.Boolean),
                new BoundBinaryOperator(SyntaxKind.BangEqualsToken, BoundBinaryOperatorKind.NotEquals, TypeSymbol.Char, TypeSymbol.Boolean),
            };

            var numericTypes = new[]
            {
                TypeSymbol.Int8, TypeSymbol.Int16, TypeSymbol.Int32, TypeSymbol.Int64,
                TypeSymbol.UInt8, TypeSymbol.UInt16, TypeSymbol.UInt32, TypeSymbol.UInt64,
                TypeSymbol.Float, TypeSymbol.Double,
            };

            foreach (var t in numericTypes)
            {
                if (t.IsInteger)
                {
                    // 6e-M21 Phase 6：<32 位窄整型不注册算术/移位/位运算条目——
                    // 二元运算先经 GetBinaryNumericResultType 升到 32/64 位域再查表（C# 先升后算同构），
                    // 否则 i16*i16 等会在窄域静默截断（如 (i16)300*(i16)300=24464 假象）。
                    if (t.BitWidth >= 32)
                    {
                        ops.Add(new BoundBinaryOperator(SyntaxKind.PlusToken, BoundBinaryOperatorKind.Addition, t));
                        ops.Add(new BoundBinaryOperator(SyntaxKind.MinusToken, BoundBinaryOperatorKind.Subtraction, t));
                        ops.Add(new BoundBinaryOperator(SyntaxKind.StarToken, BoundBinaryOperatorKind.Multiplication, t));
                        ops.Add(new BoundBinaryOperator(SyntaxKind.SlashToken, BoundBinaryOperatorKind.Division, t));
                        ops.Add(new BoundBinaryOperator(SyntaxKind.PercentToken, BoundBinaryOperatorKind.Modulo, t));
                        ops.Add(new BoundBinaryOperator(SyntaxKind.ShiftLeftToken, BoundBinaryOperatorKind.ShiftLeft, t));
                        ops.Add(new BoundBinaryOperator(SyntaxKind.ShiftRightToken, BoundBinaryOperatorKind.ShiftRight, t));
                        ops.Add(new BoundBinaryOperator(SyntaxKind.AmpersandToken, BoundBinaryOperatorKind.BitwiseAnd, t));
                        ops.Add(new BoundBinaryOperator(SyntaxKind.PipeToken, BoundBinaryOperatorKind.BitwiseOr, t));
                        ops.Add(new BoundBinaryOperator(SyntaxKind.HatToken, BoundBinaryOperatorKind.BitwiseXor, t));
                    }
                }
                else
                {
                    ops.Add(new BoundBinaryOperator(SyntaxKind.PlusToken, BoundBinaryOperatorKind.Addition, t));
                    ops.Add(new BoundBinaryOperator(SyntaxKind.MinusToken, BoundBinaryOperatorKind.Subtraction, t));
                    ops.Add(new BoundBinaryOperator(SyntaxKind.StarToken, BoundBinaryOperatorKind.Multiplication, t));
                    ops.Add(new BoundBinaryOperator(SyntaxKind.SlashToken, BoundBinaryOperatorKind.Division, t));
                }

                ops.Add(new BoundBinaryOperator(SyntaxKind.EqualsEqualsToken, BoundBinaryOperatorKind.Equals, t, TypeSymbol.Boolean));
                ops.Add(new BoundBinaryOperator(SyntaxKind.BangEqualsToken, BoundBinaryOperatorKind.NotEquals, t, TypeSymbol.Boolean));
                ops.Add(new BoundBinaryOperator(SyntaxKind.LessToken, BoundBinaryOperatorKind.Less, t, TypeSymbol.Boolean));
                ops.Add(new BoundBinaryOperator(SyntaxKind.LessOrEqualsToken, BoundBinaryOperatorKind.LessOrEquals, t, TypeSymbol.Boolean));
                ops.Add(new BoundBinaryOperator(SyntaxKind.GreaterToken, BoundBinaryOperatorKind.Greater, t, TypeSymbol.Boolean));
                ops.Add(new BoundBinaryOperator(SyntaxKind.GreaterOrEqualsToken, BoundBinaryOperatorKind.GreaterOrEquals, t, TypeSymbol.Boolean));
            }

            // any：相等（6e-M19 M5-c 修：结果类型此前误为 any，致 WriteLine(if 条件等) 无法消费）
            ops.Add(new BoundBinaryOperator(SyntaxKind.EqualsEqualsToken, BoundBinaryOperatorKind.Equals, TypeSymbol.Any, TypeSymbol.Boolean));
            ops.Add(new BoundBinaryOperator(SyntaxKind.BangEqualsToken, BoundBinaryOperatorKind.NotEquals, TypeSymbol.Any, TypeSymbol.Boolean));

            // 6e-M19 M5-a：null == null / null != null（恒 true/false，运行时平凡成立）
            ops.Add(new BoundBinaryOperator(SyntaxKind.EqualsEqualsToken, BoundBinaryOperatorKind.Equals, TypeSymbol.Null, TypeSymbol.Boolean));
            ops.Add(new BoundBinaryOperator(SyntaxKind.BangEqualsToken, BoundBinaryOperatorKind.NotEquals, TypeSymbol.Null, TypeSymbol.Boolean));

            return ops.ToArray();
        }

        /// <summary>6e-M19 M5-a：可空引用型（类/接口/string/数组/any）——null 比较与引用转换的合法目标。</summary>
        private static bool IsNullableReference(TypeSymbol type)
        {
            return !type.IsValueType && (type is NamedTypeSymbol || type == TypeSymbol.String || type == TypeSymbol.Any || type.ElementType != null);
        }

        public static BoundBinaryOperator? Bind(SyntaxKind syntaxKind, TypeSymbol leftType, TypeSymbol rightType)
        {
            if (leftType is NamedTypeSymbol { TypeKind: TypeKind.Enum } enumType && leftType == rightType)
            {
                if (syntaxKind == SyntaxKind.EqualsEqualsToken)
                    return new BoundBinaryOperator(syntaxKind, BoundBinaryOperatorKind.Equals, enumType, TypeSymbol.Boolean);
                if (syntaxKind == SyntaxKind.BangEqualsToken)
                    return new BoundBinaryOperator(syntaxKind, BoundBinaryOperatorKind.NotEquals, enumType, TypeSymbol.Boolean);
            }

            // 6e-M19 M2-c：类类型 == / != → 引用相等（动态合成，仿 enum 先例）。
            // 条件：双侧均为类（含 System.Object/接口/外部类），且存在继承关系（同型或一侧可隐式转换到另一侧）。
            // string/值类型/any 走既有值比较表，不受影响。
            if (leftType is NamedTypeSymbol { IsValueType: false } leftClass && rightType is NamedTypeSymbol { IsValueType: false } rightClass &&
                leftType != TypeSymbol.String && rightType != TypeSymbol.String &&
                (leftClass == rightClass || leftClass.IsBaseOf(rightClass) || rightClass.IsBaseOf(leftClass)))
            {
                var referenceKind = syntaxKind switch
                {
                    SyntaxKind.EqualsEqualsToken => BoundBinaryOperatorKind.ReferenceEquals,
                    SyntaxKind.BangEqualsToken => BoundBinaryOperatorKind.ReferenceNotEquals,
                    _ => (BoundBinaryOperatorKind?)null,
                };

                if (referenceKind != null)
                {
                    return new BoundBinaryOperator(syntaxKind, referenceKind.Value, leftClass, rightClass, TypeSymbol.Boolean);
                }
            }

            // 6e-M22 C5+ 多播事件：函数值 == / != → 引用相等（-= 按引用移除首个匹配订阅者）。
            // FunctionTypeSymbol 工厂缓存 ⇒ 结构同形即同一实例，符号引用比较即可判同形；发射层复用既有 ReferenceEquals 三后端路径。
            if (leftType is FunctionTypeSymbol leftFn && rightType is FunctionTypeSymbol rightFn && leftFn == rightFn)
            {
                var functionValueKind = syntaxKind switch
                {
                    SyntaxKind.EqualsEqualsToken => BoundBinaryOperatorKind.ReferenceEquals,
                    SyntaxKind.BangEqualsToken => BoundBinaryOperatorKind.ReferenceNotEquals,
                    _ => (BoundBinaryOperatorKind?)null,
                };

                if (functionValueKind != null)
                {
                    return new BoundBinaryOperator(syntaxKind, functionValueKind.Value, leftFn, rightFn, TypeSymbol.Boolean);
                }
            }

            // 6e-M19 M5-a：null 字面量与可空引用型（类/接口/string/数组/any）== / != → 引用相等。
            // 不经值语义路径（string 值比较/native StrEquals 对单侧 null 会解引用崩溃），指针比较三后端天然一致。
            if (syntaxKind == SyntaxKind.EqualsEqualsToken || syntaxKind == SyntaxKind.BangEqualsToken)
            {
                var referenceKind = syntaxKind == SyntaxKind.EqualsEqualsToken
                    ? BoundBinaryOperatorKind.ReferenceEquals
                    : BoundBinaryOperatorKind.ReferenceNotEquals;

                if (leftType == TypeSymbol.Null && IsNullableReference(rightType))
                {
                    return new BoundBinaryOperator(syntaxKind, referenceKind, leftType, rightType, TypeSymbol.Boolean);
                }

                if (rightType == TypeSymbol.Null && IsNullableReference(leftType))
                {
                    return new BoundBinaryOperator(syntaxKind, referenceKind, leftType, rightType, TypeSymbol.Boolean);
                }
            }

            foreach (var op in _operators)
            {
                if (op.SyntaxKind == syntaxKind && op.LeftType == leftType && op.RightType == rightType)
                {
                    return op;
                }
            }

            return null;
        }
    }
}
