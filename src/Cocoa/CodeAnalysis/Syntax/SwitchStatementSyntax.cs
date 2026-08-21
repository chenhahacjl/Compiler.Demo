using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>switch 语句：`switch (value) { case ...: ... default: ... }`。</summary>
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
    }
}
