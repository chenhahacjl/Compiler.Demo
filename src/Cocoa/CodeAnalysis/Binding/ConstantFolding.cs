using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 常量折叠（6e-M21 Phase 2）：按操作数类型的计算域分发——
    /// 有符号整数在 long 域、无符号在 ulong 域（右移为逻辑移位）、f32 在 float 域、f64 在 double 域；
    /// 结果按目标位宽截断归位（装箱为对应 CLR 原生类型）。
    /// </summary>
    internal static class ConstantFolding
    {
        public static BoundConstant? Fold(BoundUnaryOperator op, BoundExpression operand)
        {
            if (operand.ConstantValue == null)
            {
                return null;
            }

            var value = operand.ConstantValue.Value;
            var type = operand.Type;

            switch (op.Kind)
            {
                case BoundUnaryOperatorKind.Identity:
                    return new BoundConstant(value);
                case BoundUnaryOperatorKind.Negation:
                    if (type.IsInteger && !type.IsPlaceholder128)
                    {
                        return type.IsSigned
                            ? new BoundConstant(NumericBox.Box(type, unchecked(-NumericBox.ToSigned64(value))))
                            : new BoundConstant(NumericBox.Box(type, unchecked(0UL - NumericBox.ToUnsigned64(value))));
                    }

                    if (type == TypeSymbol.Float)
                        return new BoundConstant(-(float)value);
                    if (type == TypeSymbol.Double)
                        return new BoundConstant(-(double)value);
                    break;
                case BoundUnaryOperatorKind.LogicalNegation:
                    return new BoundConstant(!(bool)value);
                case BoundUnaryOperatorKind.OnesComplement:
                    if (type.IsInteger && !type.IsPlaceholder128)
                    {
                        return type.IsSigned
                            ? new BoundConstant(NumericBox.Box(type, ~NumericBox.ToSigned64(value)))
                            : new BoundConstant(NumericBox.Box(type, ~NumericBox.ToUnsigned64(value)));
                    }

                    break;
            }

            throw new Exception($"Unexcepted unary operator {op.Kind}");
        }

        public static BoundConstant? Fold(BoundExpression left, BoundBinaryOperator op, BoundExpression right)
        {
            var leftConstant = left.ConstantValue;
            var rightConstant = right.ConstantValue;

            // Special case && and || because there cases where only need one
            // side to be known.

            if (op.Kind == BoundBinaryOperatorKind.LogicalAnd)
            {
                // false && right = false  #  left && false = false
                if (leftConstant != null && !(bool)leftConstant.Value ||
                    rightConstant != null && !(bool)rightConstant.Value)
                {
                    return new BoundConstant(false);
                }
            }

            if (op.Kind == BoundBinaryOperatorKind.LogicalOr)
            {
                // true || right = true  #  left || true = true
                if (leftConstant != null && (bool)leftConstant.Value ||
                    rightConstant != null && (bool)rightConstant.Value)
                {
                    return new BoundConstant(true);
                }
            }

            if (leftConstant == null || rightConstant == null)
            {
                return null;
            }

            var type = left.Type;

            // 6e-M21 Phase 1 的二元提升保证到达此处时 left.Type == right.Type == 公共计算类型。
            if (type.IsInteger && !type.IsPlaceholder128)
            {
                return FoldInteger(op, leftConstant.Value, rightConstant.Value, type);
            }

            if (type == TypeSymbol.Float)
            {
                return FoldFloat32(op, leftConstant.Value, rightConstant.Value);
            }

            switch (op.Kind)
            {
                case BoundBinaryOperatorKind.Addition:
                    if (left.Type == TypeSymbol.Int32)
                        return new BoundConstant((int)leftConstant.Value + (int)rightConstant.Value);
                    if (left.Type == TypeSymbol.Int64)
                        return new BoundConstant((long)leftConstant.Value + (long)rightConstant.Value);
                    if (left.Type == TypeSymbol.Double)
                        return new BoundConstant((double)leftConstant.Value + (double)rightConstant.Value);
                    // string + double：运行时按定点格式拼接，编译期不折叠（避免与 .NET ToString 格式不一致）
                    if (right.Type == TypeSymbol.Double)
                        return null;
                    return new BoundConstant((string)leftConstant.Value + (string)rightConstant.Value);
                case BoundBinaryOperatorKind.Subtraction:
                    if (left.Type == TypeSymbol.Double)
                        return new BoundConstant((double)leftConstant.Value - (double)rightConstant.Value);
                    if (left.Type == TypeSymbol.Int64)
                        return new BoundConstant((long)leftConstant.Value - (long)rightConstant.Value);
                    return new BoundConstant((int)leftConstant.Value - (int)rightConstant.Value);
                case BoundBinaryOperatorKind.Multiplication:
                    if (left.Type == TypeSymbol.Double)
                        return new BoundConstant((double)leftConstant.Value * (double)rightConstant.Value);
                    if (left.Type == TypeSymbol.Int64)
                        return new BoundConstant((long)leftConstant.Value * (long)rightConstant.Value);
                    return new BoundConstant((int)leftConstant.Value * (int)rightConstant.Value);
                case BoundBinaryOperatorKind.Division:
                    if (left.Type == TypeSymbol.Double)
                        return new BoundConstant((double)leftConstant.Value / (double)rightConstant.Value);
                    // 除零不折叠，交给运行时 DivByZero 处理
                    if (left.Type == TypeSymbol.Int64)
                        return (long)rightConstant.Value == 0 ? null : new BoundConstant((long)leftConstant.Value / (long)rightConstant.Value);
                    return (int)rightConstant.Value == 0 ? null : new BoundConstant((int)leftConstant.Value / (int)rightConstant.Value);
                case BoundBinaryOperatorKind.Modulo:
                    // 模零不折叠，交给运行时 DivByZero 处理
                    if (left.Type == TypeSymbol.Int64)
                        return (long)rightConstant.Value == 0 ? null : new BoundConstant((long)leftConstant.Value % (long)rightConstant.Value);
                    return (int)rightConstant.Value == 0 ? null : new BoundConstant((int)leftConstant.Value % (int)rightConstant.Value);
                case BoundBinaryOperatorKind.ShiftLeft:
                    if (left.Type == TypeSymbol.Int64)
                        return new BoundConstant((long)leftConstant.Value << (int)rightConstant.Value);
                    return new BoundConstant((int)leftConstant.Value << (int)rightConstant.Value);
                case BoundBinaryOperatorKind.ShiftRight:
                    if (left.Type == TypeSymbol.Int64)
                        return new BoundConstant((long)leftConstant.Value >> (int)rightConstant.Value);
                    return new BoundConstant((int)leftConstant.Value >> (int)rightConstant.Value);
                case BoundBinaryOperatorKind.BitwiseAnd:
                    if (left.Type == TypeSymbol.Int32 || left.Type == TypeSymbol.Int64)
                        return left.Type == TypeSymbol.Int64
                            ? new BoundConstant((long)leftConstant.Value & (long)rightConstant.Value)
                            : new BoundConstant((int)leftConstant.Value & (int)rightConstant.Value);
                    return new BoundConstant((bool)leftConstant.Value & (bool)rightConstant.Value);
                case BoundBinaryOperatorKind.BitwiseOr:
                    if (left.Type == TypeSymbol.Int32 || left.Type == TypeSymbol.Int64)
                        return left.Type == TypeSymbol.Int64
                            ? new BoundConstant((long)leftConstant.Value | (long)rightConstant.Value)
                            : new BoundConstant((int)leftConstant.Value | (int)rightConstant.Value);
                    return new BoundConstant((bool)leftConstant.Value | (bool)rightConstant.Value);
                case BoundBinaryOperatorKind.BitwiseXor:
                    if (left.Type == TypeSymbol.Int32 || left.Type == TypeSymbol.Int64)
                        return left.Type == TypeSymbol.Int64
                            ? new BoundConstant((long)leftConstant.Value ^ (long)rightConstant.Value)
                            : new BoundConstant((int)leftConstant.Value ^ (int)rightConstant.Value);
                    return new BoundConstant((bool)leftConstant.Value ^ (bool)rightConstant.Value);
                case BoundBinaryOperatorKind.LogicalAnd:
                    return new BoundConstant((bool)leftConstant.Value && (bool)rightConstant.Value);
                case BoundBinaryOperatorKind.LogicalOr:
                    return new BoundConstant((bool)leftConstant.Value || (bool)rightConstant.Value);
                case BoundBinaryOperatorKind.Equals:
                    return new BoundConstant(Equals(leftConstant.Value, rightConstant.Value));
                case BoundBinaryOperatorKind.NotEquals:
                    return new BoundConstant(!Equals(leftConstant.Value, rightConstant.Value));
                case BoundBinaryOperatorKind.Less:
                    if (left.Type == TypeSymbol.Double)
                        return new BoundConstant((double)leftConstant.Value < (double)rightConstant.Value);
                    if (left.Type == TypeSymbol.Int64)
                        return new BoundConstant((long)leftConstant.Value < (long)rightConstant.Value);
                    return new BoundConstant((int)leftConstant.Value < (int)rightConstant.Value);
                case BoundBinaryOperatorKind.LessOrEquals:
                    if (left.Type == TypeSymbol.Double)
                        return new BoundConstant((double)leftConstant.Value <= (double)rightConstant.Value);
                    if (left.Type == TypeSymbol.Int64)
                        return new BoundConstant((long)leftConstant.Value <= (long)rightConstant.Value);
                    return new BoundConstant((int)leftConstant.Value <= (int)rightConstant.Value);
                case BoundBinaryOperatorKind.Greater:
                    if (left.Type == TypeSymbol.Double)
                        return new BoundConstant((double)leftConstant.Value > (double)rightConstant.Value);
                    if (left.Type == TypeSymbol.Int64)
                        return new BoundConstant((long)leftConstant.Value > (long)rightConstant.Value);
                    return new BoundConstant((int)leftConstant.Value > (int)rightConstant.Value);
                case BoundBinaryOperatorKind.GreaterOrEquals:
                    if (left.Type == TypeSymbol.Double)
                        return new BoundConstant((double)leftConstant.Value >= (double)rightConstant.Value);
                    if (left.Type == TypeSymbol.Int64)
                        return new BoundConstant((long)leftConstant.Value >= (long)rightConstant.Value);
                    return new BoundConstant((int)leftConstant.Value >= (int)rightConstant.Value);
                default:
                    throw new Exception($"Unexpected binary operator {op.Kind}");
            }
        }

        /// <summary>整数二元折叠：有符号在 long 域、无符号在 ulong 域（除零不折叠、移位计数掩码、无符号右移为逻辑移位）。</summary>
        private static BoundConstant? FoldInteger(BoundBinaryOperator op, object lv, object rv, TypeSymbol type)
        {
            if (type.IsSigned)
            {
                var a = NumericBox.ToSigned64(lv);
                var b = NumericBox.ToSigned64(rv);
                switch (op.Kind)
                {
                    case BoundBinaryOperatorKind.Addition: return new BoundConstant(NumericBox.Box(type, unchecked(a + b)));
                    case BoundBinaryOperatorKind.Subtraction: return new BoundConstant(NumericBox.Box(type, unchecked(a - b)));
                    case BoundBinaryOperatorKind.Multiplication: return new BoundConstant(NumericBox.Box(type, unchecked(a * b)));
                    case BoundBinaryOperatorKind.Division: return b == 0 ? null : new BoundConstant(NumericBox.Box(type, a / b));
                    case BoundBinaryOperatorKind.Modulo: return b == 0 ? null : new BoundConstant(NumericBox.Box(type, a % b));
                    case BoundBinaryOperatorKind.ShiftLeft: return new BoundConstant(NumericBox.Box(type, a << ((int)b & 63)));
                    case BoundBinaryOperatorKind.ShiftRight: return new BoundConstant(NumericBox.Box(type, a >> ((int)b & 63)));
                    case BoundBinaryOperatorKind.BitwiseAnd: return new BoundConstant(NumericBox.Box(type, a & b));
                    case BoundBinaryOperatorKind.BitwiseOr: return new BoundConstant(NumericBox.Box(type, a | b));
                    case BoundBinaryOperatorKind.BitwiseXor: return new BoundConstant(NumericBox.Box(type, a ^ b));
                    case BoundBinaryOperatorKind.Equals: return new BoundConstant(a == b);
                    case BoundBinaryOperatorKind.NotEquals: return new BoundConstant(a != b);
                    case BoundBinaryOperatorKind.Less: return new BoundConstant(a < b);
                    case BoundBinaryOperatorKind.LessOrEquals: return new BoundConstant(a <= b);
                    case BoundBinaryOperatorKind.Greater: return new BoundConstant(a > b);
                    case BoundBinaryOperatorKind.GreaterOrEquals: return new BoundConstant(a >= b);
                }
            }
            else
            {
                var a = NumericBox.ToUnsigned64(lv);
                var b = NumericBox.ToUnsigned64(rv);
                switch (op.Kind)
                {
                    case BoundBinaryOperatorKind.Addition: return new BoundConstant(NumericBox.Box(type, unchecked(a + b)));
                    case BoundBinaryOperatorKind.Subtraction: return new BoundConstant(NumericBox.Box(type, unchecked(a - b)));
                    case BoundBinaryOperatorKind.Multiplication: return new BoundConstant(NumericBox.Box(type, unchecked(a * b)));
                    case BoundBinaryOperatorKind.Division: return b == 0UL ? null : new BoundConstant(NumericBox.Box(type, a / b));
                    case BoundBinaryOperatorKind.Modulo: return b == 0UL ? null : new BoundConstant(NumericBox.Box(type, a % b));
                    case BoundBinaryOperatorKind.ShiftLeft: return new BoundConstant(NumericBox.Box(type, a << ((int)b & 63)));
                    case BoundBinaryOperatorKind.ShiftRight: return new BoundConstant(NumericBox.Box(type, a >> ((int)b & 63)));
                    case BoundBinaryOperatorKind.BitwiseAnd: return new BoundConstant(NumericBox.Box(type, a & b));
                    case BoundBinaryOperatorKind.BitwiseOr: return new BoundConstant(NumericBox.Box(type, a | b));
                    case BoundBinaryOperatorKind.BitwiseXor: return new BoundConstant(NumericBox.Box(type, a ^ b));
                    case BoundBinaryOperatorKind.Equals: return new BoundConstant(a == b);
                    case BoundBinaryOperatorKind.NotEquals: return new BoundConstant(a != b);
                    case BoundBinaryOperatorKind.Less: return new BoundConstant(a < b);
                    case BoundBinaryOperatorKind.LessOrEquals: return new BoundConstant(a <= b);
                    case BoundBinaryOperatorKind.Greater: return new BoundConstant(a > b);
                    case BoundBinaryOperatorKind.GreaterOrEquals: return new BoundConstant(a >= b);
                }
            }

            throw new Exception($"Unexpected integer binary operator {op.Kind}");
        }

        /// <summary>f32 折叠：float 域四则与比较。</summary>
        private static BoundConstant? FoldFloat32(BoundBinaryOperator op, object lv, object rv)
        {
            var a = (float)lv;
            var b = (float)rv;
            switch (op.Kind)
            {
                case BoundBinaryOperatorKind.Addition: return new BoundConstant(a + b);
                case BoundBinaryOperatorKind.Subtraction: return new BoundConstant(a - b);
                case BoundBinaryOperatorKind.Multiplication: return new BoundConstant(a * b);
                case BoundBinaryOperatorKind.Division: return new BoundConstant(a / b);
                case BoundBinaryOperatorKind.Equals: return new BoundConstant(a == b);
                case BoundBinaryOperatorKind.NotEquals: return new BoundConstant(a != b);
                case BoundBinaryOperatorKind.Less: return new BoundConstant(a < b);
                case BoundBinaryOperatorKind.LessOrEquals: return new BoundConstant(a <= b);
                case BoundBinaryOperatorKind.Greater: return new BoundConstant(a > b);
                case BoundBinaryOperatorKind.GreaterOrEquals: return new BoundConstant(a >= b);
            }

            throw new Exception($"Unexpected float binary operator {op.Kind}");
        }

        /// <summary>按类型位宽截断归位：有符号装箱 sbyte/short/int/long，无符号装箱 byte/ushort/uint/ulong。</summary>
        private static object Box(TypeSymbol type, long value)
        {
            return type.IsSigned ? BoxSigned(type, value) : BoxUnsigned(type, unchecked((ulong)value));
        }

        private static object BoxSigned(TypeSymbol type, long value)
        {
            // 注意：各 arm 必须显式转 object——否则 switch 表达式的自然类型会被推断为公共类型 long，
            // 导致 (int) 归位值被静默提升回 long（6e-M21 Phase2 踩坑记录）
            return type.BitWidth switch
            {
                8 => (object)(sbyte)value,
                16 => (object)(short)value,
                32 => (object)(int)value,
                _ => value,
            };
        }

        private static object Box(TypeSymbol type, ulong value)
        {
            return type.IsSigned ? Box(type, unchecked((long)value)) : BoxUnsigned(type, value);
        }

        private static object BoxUnsigned(TypeSymbol type, ulong value)
        {
            // 同 BoxSigned：arm 显式转 object，避免公共类型推断为 ulong
            return type.BitWidth switch
            {
                8 => (object)(byte)value,
                16 => (object)(ushort)value,
                32 => (object)(uint)value,
                _ => value,
            };
        }

        private static long ToSigned64(object value) => value switch
        {
            int i => i,
            long l => l,
            char c => c,
            sbyte sb => sb,
            short s => s,
            uint u => unchecked((long)u),
            byte b => b,
            ushort us => us,
            ulong ul => unchecked((long)ul),
            _ => throw new System.InvalidOperationException($"Not an integer constant: {value}"),
        };

        private static ulong ToUnsigned64(object value) => value switch
        {
            uint u => u,
            ulong ul => ul,
            byte b => b,
            ushort us => us,
            int i => unchecked((ulong)i),
            long l => unchecked((ulong)l),
            char c => c,
            sbyte sb => unchecked((ulong)sb),
            short s => unchecked((ulong)s),
            _ => throw new System.InvalidOperationException($"Not an integer constant: {value}"),
        };
    }
}


