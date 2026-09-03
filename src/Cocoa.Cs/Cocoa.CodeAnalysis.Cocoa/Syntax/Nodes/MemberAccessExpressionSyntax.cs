using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 鎴愬憳璁块棶琛ㄨ揪寮忚娉曪細arr.Length
    /// </summary>
    public sealed partial class MemberAccessExpressionSyntax : ExpressionSyntax
    {
        internal MemberAccessExpressionSyntax(SyntaxTree syntaxTree, ExpressionSyntax expression, SyntaxToken dotToken, SyntaxToken identifierToken)
            : base(syntaxTree)
        {
            Expression = expression;
            DotToken = dotToken;
            IdentifierToken = identifierToken;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.MemberAccessExpression;

        public ExpressionSyntax Expression { get; }
        public SyntaxToken DotToken { get; }
        public SyntaxToken IdentifierToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Expression;
            yield return DotToken;
            yield return IdentifierToken;
        }
    }
}

