using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    public sealed partial class GlobalStatementSyntax : MemberSyntax
    {
        internal GlobalStatementSyntax(SyntaxTree syntaxTree, StatementSyntax statement)
            : base(syntaxTree, ImmutableArray<SyntaxToken>.Empty)
        {
            Statement = statement;
        }

        public override SyntaxKind Kind => SyntaxKind.GlobalStatement;

        public StatementSyntax Statement { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            foreach (var child in Modifiers)
            {
                yield return child;
            }
            yield return Statement;
        }
    }
}
