namespace Cocoa.CodeAnalysis.Syntax
{
    public sealed class IfStatementSyntax : StatementSyntax
    {
        public IfStatementSyntax(SyntaxToken keyword, ExpressionSyntax condition, StatementSyntax thenStatement, ElseClauseSyntax elseClause)
        {
            Keyword = keyword;
            Condition = condition;
            ThenStatement = thenStatement;
            ElseClause = elseClause;
        }

        public override SyntaxKind Kind => SyntaxKind.IfStatement;

        public SyntaxToken Keyword { get; }
        public ExpressionSyntax Condition { get; }
        public StatementSyntax ThenStatement { get; }
        public ElseClauseSyntax ElseClause { get; }
    }
}
