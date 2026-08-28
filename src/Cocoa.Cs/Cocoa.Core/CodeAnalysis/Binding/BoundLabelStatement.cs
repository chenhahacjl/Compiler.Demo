using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    public sealed class BoundLabelStatement : BoundStatement
    {
        public BoundLabelStatement(SyntaxNode syntax, BoundLabel label)
            : base(syntax)
        {
            Label = label;
        }

        public override BoundNodeKind Kind => BoundNodeKind.LabelStatement;

        public BoundLabel Label { get; }
    }
}
