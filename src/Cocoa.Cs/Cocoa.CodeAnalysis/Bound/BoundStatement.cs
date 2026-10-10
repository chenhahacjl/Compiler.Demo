using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Bound
{
    public abstract class BoundStatement : BoundNode
    {
        protected BoundStatement(SyntaxNode syntax)
            : base(syntax)
        {
        }
    }
}
