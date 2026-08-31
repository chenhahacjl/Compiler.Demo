namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 成员访问表达式语法：arr.Length
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

        public override SyntaxKind Kind => SyntaxKind.MemberAccessExpression;

        public ExpressionSyntax Expression { get; }
        public SyntaxToken DotToken { get; }
        public SyntaxToken IdentifierToken { get; }
    }
}