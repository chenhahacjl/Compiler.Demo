using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundErrorExpression : BoundExpression
    {
        // TODO: Should the error expression accept an array of bound nodes so that we don't drop
        //       parts of the bound tree on the floor?

        public BoundErrorExpression(SyntaxNode syntax)
            : base(syntax)
        {
        }

        public override BoundNodeKind Kind => BoundNodeKind.ErrorExpression;
        public override TypeSymbol Type => TypeSymbol.Error;
    }
}
