namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// C 风格 for 循环 `for (init; cond; update) body`，三个部分均可省略。
    /// </summary>
    public sealed partial class CStyleForStatementSyntax : StatementSyntax
    {
        internal CStyleForStatementSyntax(
            SyntaxTree syntaxTree,
            SyntaxToken keyword,
            SyntaxToken openParenToken,
            StatementSyntax? init,
            SyntaxToken? semicolonToken1,
            ExpressionSyntax? condition,
            SyntaxToken? semicolonToken2,
            ExpressionSyntax? update,
            SyntaxToken closeParenToken,
            StatementSyntax body)
            : base(syntaxTree)
        {
            Keyword = keyword;
            OpenParenToken = openParenToken;
            Init = init;
            SemicolonToken1 = semicolonToken1;
            Condition = condition;
            SemicolonToken2 = semicolonToken2;
            Update = update;
            CloseParenToken = closeParenToken;
            Body = body;
        }

        public override SyntaxKind Kind => SyntaxKind.CStyleForStatement;

        public SyntaxToken Keyword { get; }
        public SyntaxToken OpenParenToken { get; }
        public StatementSyntax? Init { get; }
        public SyntaxToken? SemicolonToken1 { get; }
        public ExpressionSyntax? Condition { get; }
        public SyntaxToken? SemicolonToken2 { get; }
        public ExpressionSyntax? Update { get; }
        public SyntaxToken CloseParenToken { get; }
        public StatementSyntax Body { get; }
    }
}
