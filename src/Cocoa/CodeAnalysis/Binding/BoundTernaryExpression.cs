using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundTernaryExpression : BoundExpression
    {
        public BoundTernaryExpression(SyntaxNode syntax, BoundExpression condition, BoundExpression thenExpression, BoundExpression elseExpression)
            : base(syntax)
        {
            Condition = condition;
            ThenExpression = thenExpression;
            ElseExpression = elseExpression;
        }

        public override BoundNodeKind Kind => BoundNodeKind.TernaryExpression;
        public override TypeSymbol Type => ThenExpression.Type;

        public BoundExpression Condition { get; }
        public BoundExpression ThenExpression { get; }
        public BoundExpression ElseExpression { get; }
    }
}
