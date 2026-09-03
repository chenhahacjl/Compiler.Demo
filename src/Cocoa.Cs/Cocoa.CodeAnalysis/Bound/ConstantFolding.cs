using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 常量折叠（6e-M21 Phase 2）：运算语义已统一收口到 <see cref="PrimitiveEval"/>（5.4b 单一求值核），
    /// 本类只保留折叠层特有逻辑——&amp;&amp;/|| 单侧已知短路特判，以及"不可折叠（模零等）返回 null"。
    /// </summary>
    internal static class ConstantFolding
    {
        public static BoundConstant? Fold(BoundUnaryOperator op, BoundExpression operand)
        {
            if (operand.ConstantValue == null)
            {
                return null;
            }

            // 6e-M21 Phase 7：窄整型一元结果升 Int32——归位以结果类型为准
            // 分派域用操作数静态类型（与折叠层既有行为一致）
            return new BoundConstant(PrimitiveEval.Unary(op.Kind, operand.Type, op.ResultType, operand.ConstantValue.Value)!);
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

            // 6e-M21 Phase 1 的二元提升保证到达此处时 left.Type == right.Type == 公共计算类型。
            // NotComputable（模零）/Unsupported（string+double 定点拼接、引用相等）→ 不折叠，交给运行时
            var status = PrimitiveEval.TryBinary(op.Kind, left.Type, leftConstant.Value, rightConstant.Value, out var result);
            return status == PrimitiveEvalStatus.Computed ? new BoundConstant(result!) : null;
        }
    }
}
