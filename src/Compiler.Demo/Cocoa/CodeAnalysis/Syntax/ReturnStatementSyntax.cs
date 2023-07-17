namespace Cocoa.CodeAnalysis.Syntax
{
    public sealed class ReturnStatementSyntax : StatementSyntax
    {
        public ReturnStatementSyntax(SyntaxToken keyword, ExpressionSyntax expression)
        {
            Keyword = keyword;
            Expression = expression;
        }

        public override SyntaxKind Kind => SyntaxKind.ReturnStatement;

        public SyntaxToken Keyword { get; }
        public ExpressionSyntax Expression { get; }
    }
}
