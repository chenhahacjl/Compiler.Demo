using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundSequencePointStatement : BoundStatement
    {
        public BoundSequencePointStatement(SyntaxNode syntax, BoundStatement statement, TextLocation location)
            : base(syntax)
        {
            Statement = statement;
            Location = location;
        }

        public override BoundNodeKind Kind => BoundNodeKind.SequencePointStatement;

        public BoundStatement Statement { get; }
        public TextLocation Location { get; }
    }
}