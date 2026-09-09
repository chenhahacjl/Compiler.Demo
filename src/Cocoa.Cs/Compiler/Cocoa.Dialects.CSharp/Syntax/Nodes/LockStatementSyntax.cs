using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    public sealed partial class LockStatementSyntax : StatementSyntax
    {
        internal LockStatementSyntax(SyntaxTree syntaxTree, SyntaxToken lockKeyword, SyntaxToken openParenToken,
            ExpressionSyntax expression, SyntaxToken closeParenToken, BlockStatementSyntax body)
            : base(syntaxTree)
        {
            LockKeyword = lockKeyword;
            OpenParenToken = openParenToken;
            Expression = expression;
            CloseParenToken = closeParenToken;
            Body = body;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.LockStatement;

        public SyntaxToken LockKeyword { get; }
        public SyntaxToken OpenParenToken { get; }
        public ExpressionSyntax Expression { get; }
        public SyntaxToken CloseParenToken { get; }
        public BlockStatementSyntax Body { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return LockKeyword;
            yield return OpenParenToken;
            yield return Expression;
            yield return CloseParenToken;
            yield return Body;
        }
    }
}
