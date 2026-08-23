using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定一元表达式
    /// </summary>
    internal sealed class BoundUnaryExpression : BoundExpression
    {
        public BoundUnaryExpression(SyntaxNode syntax, BoundUnaryOperator op, BoundExpression operand)
            : base(syntax)
        {
            Op = op;
            Operand = operand;

            ConstantValue = ConstantFolding.Fold(op, operand);
        }

        public override BoundNodeKind Kind => BoundNodeKind.UnaryExpression;
        public override TypeSymbol Type => Op.ResultType;

        public BoundUnaryOperator Op { get; }
        public BoundExpression Operand { get; }

        public override BoundConstant? ConstantValue { get; }
    }
}
