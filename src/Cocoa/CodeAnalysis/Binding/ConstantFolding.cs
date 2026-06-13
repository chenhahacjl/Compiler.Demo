using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Binding
{
    internal static class ConstantFolding
    {
        public static BoundConstant ComputeConstanct(BoundUnaryOperator op, BoundExpression operand)
        {
            var operandConstant = operand.ConstantValue;

            if (operand.ConstantValue != null)
            {
                switch (op.Kind)
                {
                    case BoundUnaryOperatorKind.Identity:
                        return new BoundConstant((int)operandConstant.Value);
                    case BoundUnaryOperatorKind.Negation:
                        return new BoundConstant(-(int)operandConstant.Value);
                    case BoundUnaryOperatorKind.LogicalNegation:
                        return new BoundConstant(!(bool)operandConstant.Value);
                    case BoundUnaryOperatorKind.OnesComplement:
                        return new BoundConstant(~(int)operandConstant.Value);
                    default:
                        throw new Exception($"Unexcepted unary operator {op.Kind}");
                }
            }

            return null;
        }

        public static BoundConstant ComputeConstanct(BoundExpression left, BoundBinaryOperator op, BoundExpression right)
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
                    return left.Type == TypeSymbol.Int32 ?
                        new BoundConstant((int)leftConstant.Value + (int)rightConstant.Value) :
                        new BoundConstant((string)leftConstant.Value + (string)rightConstant.Value);
                case BoundBinaryOperatorKind.Subtraction:
                    return new BoundConstant((int)leftConstant.Value - (int)rightConstant.Value);
                case BoundBinaryOperatorKind.Multiplication:
                    return new BoundConstant((int)leftConstant.Value * (int)rightConstant.Value);
                case BoundBinaryOperatorKind.Division:
                    return new BoundConstant((int)leftConstant.Value / (int)rightConstant.Value);
                case BoundBinaryOperatorKind.BitwiseAnd:
                    return left.Type == TypeSymbol.Int32 ?
                        new BoundConstant((int)leftConstant.Value & (int)rightConstant.Value) :
                        new BoundConstant((bool)leftConstant.Value & (bool)rightConstant.Value);
                case BoundBinaryOperatorKind.BitwiseOr:
                    return left.Type == TypeSymbol.Int32 ?
                        new BoundConstant((int)leftConstant.Value | (int)rightConstant.Value) :
                        new BoundConstant((bool)leftConstant.Value | (bool)rightConstant.Value);
                case BoundBinaryOperatorKind.BitwiseXor:
                    return left.Type == TypeSymbol.Int32 ?
                        new BoundConstant((int)leftConstant.Value ^ (int)rightConstant.Value) :
                        new BoundConstant((bool)leftConstant.Value ^ (bool)rightConstant.Value);
                case BoundBinaryOperatorKind.LogicalAnd:
                    return new BoundConstant((bool)leftConstant.Value && (bool)rightConstant.Value);
                case BoundBinaryOperatorKind.LogicalOr:
                    return new BoundConstant((bool)leftConstant.Value || (bool)rightConstant.Value);
                case BoundBinaryOperatorKind.Equals:
                    return new BoundConstant(Equals(leftConstant.Value, rightConstant.Value));
                case BoundBinaryOperatorKind.NotEquals:
                    return new BoundConstant(!Equals(leftConstant.Value, rightConstant.Value));
                case BoundBinaryOperatorKind.Less:
                    return new BoundConstant((int)leftConstant.Value < (int)rightConstant.Value);
                case BoundBinaryOperatorKind.LessOrEquals:
                    return new BoundConstant((int)leftConstant.Value <= (int)rightConstant.Value);
                case BoundBinaryOperatorKind.Greater:
                    return new BoundConstant((int)leftConstant.Value > (int)rightConstant.Value);
                case BoundBinaryOperatorKind.GreaterOrEquals:
                    return new BoundConstant((int)leftConstant.Value >= (int)rightConstant.Value);
                default:
                    throw new Exception($"Unexpected binary operator {op.Kind}");
            }
        }
    }
}