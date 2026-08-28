using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    public abstract class BoundStatement : BoundNode
    {
        protected BoundStatement(SyntaxNode syntax)
            : base(syntax)
        {
        }
    }
}
