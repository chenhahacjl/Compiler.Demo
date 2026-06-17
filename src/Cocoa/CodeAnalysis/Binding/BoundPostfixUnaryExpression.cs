using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundPostfixUnaryExpression : BoundExpression
    {
        public BoundPostfixUnaryExpression(SyntaxNode syntax, BoundUnaryOperator op, BoundExpression operand)
            : base(syntax)
        {
            Op = op;
            Operand = operand;
        }

        public override BoundNodeKind Kind => BoundNodeKind.PostfixUnaryExpression;
        public override TypeSymbol Type => Op.ResultType;

        public BoundUnaryOperator Op { get; }
        public BoundExpression Operand { get; }
    }
}
