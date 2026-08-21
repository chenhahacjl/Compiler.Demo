namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>default 子句：`default : 体`。</summary>
    public sealed partial class DefaultClauseSyntax : SwitchSectionSyntax
    {
        internal DefaultClauseSyntax(SyntaxTree syntaxTree, SyntaxToken defaultKeyword, SyntaxToken colonToken, StatementSyntax body)
            : base(syntaxTree)
        {
            DefaultKeyword = defaultKeyword;
            ColonToken = colonToken;
            Body = body;
        }

        public override SyntaxKind Kind => SyntaxKind.DefaultClause;

        public SyntaxToken DefaultKeyword { get; }
        public SyntaxToken ColonToken { get; }
        public StatementSyntax Body { get; }
    }
}
