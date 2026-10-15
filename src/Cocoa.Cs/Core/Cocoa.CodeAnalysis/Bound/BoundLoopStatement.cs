using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;

namespace Cocoa.CodeAnalysis.Bound
{
    public abstract class BoundLoopStatement : BoundStatement
    {
        protected BoundLoopStatement(SyntaxNode syntax, BoundLabel breakLabel, BoundLabel continueLabel)
            : base(syntax)
        {
            BreakLabel = breakLabel;
            ContinueLabel = continueLabel;
        }

        public BoundLabel BreakLabel { get; }
        public BoundLabel ContinueLabel { get; }
    }
}
