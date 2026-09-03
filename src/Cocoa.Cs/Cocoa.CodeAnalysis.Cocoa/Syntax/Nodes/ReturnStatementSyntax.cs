using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    public sealed partial class ReturnStatementSyntax : StatementSyntax
    {
        internal ReturnStatementSyntax(SyntaxTree syntaxTree, SyntaxToken keyword, ExpressionSyntax? expression)
            : base(syntaxTree)
        {
            Keyword = keyword;
            Expression = expression;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.ReturnStatement;

        public SyntaxToken Keyword { get; }
        public ExpressionSyntax? Expression { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Keyword;
            if (Expression != null)
            {
                yield return Expression;
            }
        }
    }
}

