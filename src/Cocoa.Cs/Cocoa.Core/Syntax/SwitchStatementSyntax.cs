using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>switch 璇彞锛歚switch (value) { case ...: ... default: ... }`銆?/summary>
    public sealed partial class SwitchStatementSyntax : StatementSyntax
    {
        internal SwitchStatementSyntax(SyntaxTree syntaxTree, SyntaxToken keyword, SyntaxToken? openParenToken, ExpressionSyntax expression, SyntaxToken? closeParenToken, SyntaxToken openBraceToken, ImmutableArray<SwitchSectionSyntax> sections, SyntaxToken closeBraceToken)
            : base(syntaxTree)
        {
            Keyword = keyword;
            OpenParenToken = openParenToken;
            Expression = expression;
            CloseParenToken = closeParenToken;
            OpenBraceToken = openBraceToken;
            Sections = sections;
            CloseBraceToken = closeBraceToken;
        }

        public override SyntaxKind Kind => SyntaxKind.SwitchStatement;

        public SyntaxToken Keyword { get; }
        public SyntaxToken? OpenParenToken { get; }
        public ExpressionSyntax Expression { get; }
        public SyntaxToken? CloseParenToken { get; }
        public SyntaxToken OpenBraceToken { get; }
        public ImmutableArray<SwitchSectionSyntax> Sections { get; }
        public SyntaxToken CloseBraceToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Keyword;
            if (OpenParenToken != null)
            {
                yield return OpenParenToken;
            }
            yield return Expression;
            if (CloseParenToken != null)
            {
                yield return CloseParenToken;
            }
            yield return OpenBraceToken;
            foreach (var child in Sections)
            {
                yield return child;
            }
            yield return CloseBraceToken;
        }
    }
}
