using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    public sealed partial class IfStatementSyntax : StatementSyntax
    {
        internal IfStatementSyntax(SyntaxTree syntaxTree, SyntaxToken keyword, ExpressionSyntax condition, StatementSyntax thenStatement, ElseClauseSyntax? elseClause)
            : base(syntaxTree)
        {
            Keyword = keyword;
            Condition = condition;
            ThenStatement = thenStatement;
            ElseClause = elseClause;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.IfStatement;

        public SyntaxToken Keyword { get; }
        public ExpressionSyntax Condition { get; }
        public StatementSyntax ThenStatement { get; }
        public ElseClauseSyntax? ElseClause { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Keyword;
            yield return Condition;
            yield return ThenStatement;
            if (ElseClause != null)
            {
                yield return ElseClause;
            }
        }
    }
}

