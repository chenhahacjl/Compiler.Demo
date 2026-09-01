using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    public sealed partial class ReturnStatementSyntax : StatementSyntax
    {
        internal ReturnStatementSyntax(SyntaxTree syntaxTree, SyntaxToken keyword, ExpressionSyntax? expression)
            : base(syntaxTree)
        {
            Keyword = keyword;
            Expression = expression;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.ReturnStatement;

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

