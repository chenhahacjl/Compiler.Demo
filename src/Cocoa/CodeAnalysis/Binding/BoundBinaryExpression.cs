using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定二元表达式
    /// </summary>
    internal sealed class BoundBinaryExpression : BoundExpression
    {
        public BoundBinaryExpression(SyntaxNode syntax, BoundExpression left, BoundBinaryOperator op, BoundExpression right)
            : base(syntax)
        {
            Left = left;
            Op = op;
            Right = right;

            ConstantValue = ConstantFolding.Fold(left, op, right);
        }

        public override BoundNodeKind Kind => BoundNodeKind.BinaryExpression;
        public override TypeSymbol Type => Op.ResultType;

        public BoundExpression Left { get; }
        public BoundBinaryOperator Op { get; }
        public BoundExpression Right { get; }

        public override BoundConstant? ConstantValue { get; }
    }
}
