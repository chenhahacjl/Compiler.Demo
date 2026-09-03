using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    public sealed partial class ThrowStatementSyntax : StatementSyntax
    {
        internal ThrowStatementSyntax(SyntaxTree syntaxTree, SyntaxToken keyword, ExpressionSyntax expression)
            : base(syntaxTree)
        {
            Keyword = keyword;
            Expression = expression;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.ThrowStatement;

        public SyntaxToken Keyword { get; }
        public ExpressionSyntax Expression { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Keyword;
            yield return Expression;
        }
    }
}

