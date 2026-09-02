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
            // 6e-M21 Phase 7：窄整型一元结果升 Int32——归位以结果类型为准
            var resultType = op.ResultType;

            switch (op.Kind)
            {
                case BoundUnaryOperatorKind.Identity:
                    // 与运行期语义一致（Evaluator.Expressions）：非整数类型（bool/string/enum 等）
                    // 不做整型归位，原值直接返回，避免误入 ToSigned64 崩溃
                    if (type.IsInteger && !type.IsPlaceholder128)
                    {
                        return new BoundConstant(NumericBox.Box(resultType, NumericBox.ToSigned64(value)));
                    }

                    return new BoundConstant(value);
                case BoundUnaryOperatorKind.Negation:
                    if (type.IsInteger && !type.IsPlaceholder128)
                    {
                        return new BoundConstant(NumericBox.Box(resultType, unchecked(-NumericBox.ToSigned64(value))));
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
                        if (resultType.IsSigned)
                        {
                            return new BoundConstant(NumericBox.Box(resultType, ~NumericBox.ToSigned64(value)));
                        }

                        return new BoundConstant(NumericBox.Box(resultType, ~NumericBox.ToUnsigned64(value)));
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
                    case BoundBinaryOperatorKind.ShiftLeft: return new BoundConstant(NumericBox.Box(type, a << ((int)b & (type.BitWidth - 1))));
                    case BoundBinaryOperatorKind.ShiftRight: return new BoundConstant(NumericBox.Box(type, a >> ((int)b & (type.BitWidth - 1))));
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
                    case BoundBinaryOperatorKind.ShiftLeft: return new BoundConstant(NumericBox.Box(type, a << ((int)b & (type.BitWidth - 1))));
                    case BoundBinaryOperatorKind.ShiftRight: return new BoundConstant(NumericBox.Box(type, a >> ((int)b & (type.BitWidth - 1))));
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
    }
}


