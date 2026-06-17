using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    public sealed partial class CaseClauseSyntax : SyntaxNode
    {
        internal CaseClauseSyntax(
            SyntaxTree syntaxTree,
            SyntaxToken keyword,
            ExpressionSyntax? value,
            SyntaxToken colonToken,
            StatementSyntax statement)
            : base(syntaxTree)
        {
            Keyword = keyword;
            Value = value;
            ColonToken = colonToken;
            Statement = statement;
        }

        public override SyntaxKind Kind => SyntaxKind.CaseClause;
        public SyntaxToken Keyword { get; }
        public ExpressionSyntax? Value { get; }
        public SyntaxToken ColonToken { get; }
        public StatementSyntax Statement { get; }
    }
}
