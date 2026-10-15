using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;

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
