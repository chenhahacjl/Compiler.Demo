using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 关系模式绑定：expr is > 0 / expr is <= 10
    /// </summary>
    public sealed class BoundRelationalPattern : BoundExpression
    {
        public BoundRelationalPattern(SyntaxNode syntax, BoundExpression expression, BoundBinaryOperatorKind operatorKind, BoundExpression value)
            : base(syntax)
        {
            Expression = expression;
            OperatorKind = operatorKind;
            Value = value;
        }

        public override BoundNodeKind Kind => BoundNodeKind.RelationalPattern;
        public override TypeSymbol Type => TypeSymbol.Boolean;

        public BoundExpression Expression { get; }
        public BoundBinaryOperatorKind OperatorKind { get; }
        public BoundExpression Value { get; }
    }
}
