using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundErrorExpression : BoundExpression
    {
        public BoundErrorExpression(SyntaxNode syntax)
            : base(syntax)
        {
        }

        public override BoundNodeKind Kind => BoundNodeKind.ErrorExpression;
        public override TypeSymbol Type => TypeSymbol.Error;
    }
}
