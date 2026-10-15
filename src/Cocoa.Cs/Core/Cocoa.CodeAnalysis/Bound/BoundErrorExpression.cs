using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;

namespace Cocoa.CodeAnalysis.Bound
{
    public sealed class BoundErrorExpression : BoundExpression
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
