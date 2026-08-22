using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Binding
{
    internal static class ConstantFolding
    {
        public static BoundConstant? Fold(BoundUnaryOperator op, BoundExpression operand)
        {
            if (operand.ConstantValue != null)
            {
                switch (op.Kind)
                {
                    case BoundUnaryOperatorKind.Identity:
                        if (operand.Type == TypeSymbol.Double)
                            return new BoundConstant((double)operand.ConstantValue.Value);
                        if (operand.Type == TypeSymbol.Int64)
                            return new BoundConstant((long)operand.ConstantValue.Value);
                        return new BoundConstant((int)operand.ConstantValue.Value);
                    case BoundUnaryOperatorKind.Negation:
                        if (operand.Type == TypeSymbol.Double)
                            return new BoundConstant(-(double)operand.ConstantValue.Value);
                        if (operand.Type == TypeSymbol.Int64)
                            return new BoundConstant(-(long)operand.ConstantValue.Value);
                        return new BoundConstant(-(int)operand.ConstantValue.Value);
                    case BoundUnaryOperatorKind.LogicalNegation:
                        return new BoundConstant(!(bool)operand.ConstantValue.Value);
                    case BoundUnaryOperatorKind.OnesComplement:
                        if (operand.Type == TypeSymbol.Int64)
                            return new BoundConstant(~(long)operand.ConstantValue.Value);
                        return new BoundConstant(~(int)operand.ConstantValue.Value);
                    default:
                        throw new Exception($"Unexcepted unary operator {op.Kind}");
                }
            }

            return null;
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
    }
}