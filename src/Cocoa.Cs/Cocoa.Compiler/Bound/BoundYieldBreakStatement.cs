using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    public sealed class BoundYieldBreakStatement : BoundStatement
    {
        public BoundYieldBreakStatement(SyntaxNode syntax)
            : base(syntax)
        {
        }

        public override BoundNodeKind Kind => BoundNodeKind.YieldBreakStatement;
    }
}
