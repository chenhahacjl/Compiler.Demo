namespace Cocoa.CodeAnalysis.Syntax
{
    public sealed partial class ThrowStatementSyntax : StatementSyntax
    {
        internal ThrowStatementSyntax(SyntaxTree syntaxTree, SyntaxToken keyword, ExpressionSyntax expression)
            : base(syntaxTree)
        {
            Keyword = keyword;
            Expression = expression;
        }

        public override SyntaxKind Kind => SyntaxKind.ThrowStatement;

        public SyntaxToken Keyword { get; }
        public ExpressionSyntax Expression { get; }
    }
}
