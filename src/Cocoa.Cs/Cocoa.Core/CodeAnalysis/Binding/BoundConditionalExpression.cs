using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 三元条件表达式 `cond ? a : b`（三后端短路求值）
    /// </summary>
    public sealed class BoundConditionalExpression : BoundExpression
    {
        public BoundConditionalExpression(SyntaxNode syntax, BoundExpression condition, BoundExpression whenTrue, BoundExpression whenFalse)
            : base(syntax)
        {
            Condition = condition;
            WhenTrue = whenTrue;
            WhenFalse = whenFalse;
        }

        public override BoundNodeKind Kind => BoundNodeKind.ConditionalExpression;
        public override TypeSymbol Type => WhenTrue.Type;

        public override BoundConstant? ConstantValue
        {
            get
            {
                if (Condition.ConstantValue != null)
                {
                    return (bool)Condition.ConstantValue.Value
                        ? WhenTrue.ConstantValue
                        : WhenFalse.ConstantValue;
                }

                return null;
            }
        }

        public BoundExpression Condition { get; }
        public BoundExpression WhenTrue { get; }
        public BoundExpression WhenFalse { get; }
    }
}
