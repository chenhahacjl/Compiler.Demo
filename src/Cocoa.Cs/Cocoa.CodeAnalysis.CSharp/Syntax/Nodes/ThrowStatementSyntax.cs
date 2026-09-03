using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    public sealed partial class ThrowStatementSyntax : StatementSyntax
    {
        internal ThrowStatementSyntax(SyntaxTree syntaxTree, SyntaxToken keyword, ExpressionSyntax expression)
            : base(syntaxTree)
        {
            Keyword = keyword;
            Expression = expression;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.ThrowStatement;

        public SyntaxToken Keyword { get; }
        public ExpressionSyntax Expression { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Keyword;
            yield return Expression;
        }
    }
}

