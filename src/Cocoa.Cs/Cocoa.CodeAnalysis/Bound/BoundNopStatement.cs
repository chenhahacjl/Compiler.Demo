using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Bound
{
    public sealed class BoundNopStatement : BoundStatement
    {
        public BoundNopStatement(SyntaxNode syntax)
            : base(syntax)
        {
        }

        public override BoundNodeKind Kind => BoundNodeKind.NopStatement;
    }
}
