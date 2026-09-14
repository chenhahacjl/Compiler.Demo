using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 逻辑模式绑定：expr is > 0 and < 10 / expr is null or 0 / expr is not null
    /// </summary>
    public sealed class BoundLogicalPattern : BoundExpression
    {
        public BoundLogicalPattern(SyntaxNode syntax, BoundExpression left, BoundLogicalPatternKind operatorKind, BoundExpression right)
            : base(syntax)
        {
            Left = left;
            OperatorKind = operatorKind;
            Right = right;
        }

        /// <summary>
        /// 一元 not 模式
        /// </summary>
        public BoundLogicalPattern(SyntaxNode syntax, BoundLogicalPatternKind operatorKind, BoundExpression operand)
            : base(syntax)
        {
            OperatorKind = operatorKind;
            Operand = operand;
        }

        public override BoundNodeKind Kind => BoundNodeKind.LogicalPattern;
        public override TypeSymbol Type => TypeSymbol.Boolean;

        public BoundExpression? Left { get; }
        public BoundLogicalPatternKind OperatorKind { get; }
        public BoundExpression? Right { get; }

        /// <summary>一元 not 操作数</summary>
        public BoundExpression? Operand { get; }

        public bool IsUnary => Operand != null;
    }

    /// <summary>
    /// 逻辑模式操作符类型
    /// </summary>
    public enum BoundLogicalPatternKind
    {
        And,
        Or,
        Not
    }
}
