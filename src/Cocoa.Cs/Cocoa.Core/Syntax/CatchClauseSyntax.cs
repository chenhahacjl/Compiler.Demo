namespace Cocoa.CodeAnalysis.Syntax
{
    public sealed partial class CatchClauseSyntax : SyntaxNode
    {
        internal CatchClauseSyntax(SyntaxTree syntaxTree, SyntaxToken catchKeyword, SyntaxToken identifier, TypeClauseSyntax type, BlockStatementSyntax body)
            : base(syntaxTree)
        {
            CatchKeyword = catchKeyword;
            Identifier = identifier;
            Type = type;
            Body = body;
        }

        public override SyntaxKind Kind => SyntaxKind.CatchClause;

        public SyntaxToken CatchKeyword { get; }
        public SyntaxToken Identifier { get; }
        public TypeClauseSyntax Type { get; }
        public BlockStatementSyntax Body { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return CatchKeyword;
            yield return Identifier;
            yield return Type;
            yield return Body;
        }
    }
}
