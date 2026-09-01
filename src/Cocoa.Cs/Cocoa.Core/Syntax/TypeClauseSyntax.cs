namespace Cocoa.CodeAnalysis.Syntax
{
    public partial class TypeClauseSyntax : SyntaxNode
    {
        internal TypeClauseSyntax(SyntaxTree syntaxTree, SyntaxToken? colonToken, SyntaxToken identifier)
            : base(syntaxTree)
        {
            ColonToken = colonToken;
            Identifier = identifier;
        }

        public override SyntaxKind Kind => SyntaxKind.TypeClause;

        public SyntaxToken? ColonToken { get; }
        public SyntaxToken Identifier { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            if (ColonToken != null)
            {
                yield return ColonToken;
            }
            yield return Identifier;
        }
    }
}
