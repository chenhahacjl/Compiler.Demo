namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 成员方法调用表达式语法：s.substring(1, 3)
    /// </summary>
    public sealed partial class MemberCallExpressionSyntax : ExpressionSyntax
    {
        internal MemberCallExpressionSyntax(SyntaxTree syntaxTree, ExpressionSyntax expression, SyntaxToken dotToken, SyntaxToken identifierToken, SyntaxToken openParenthesisToken, SeparatedSyntaxList<ExpressionSyntax> arguments, SyntaxToken closeParenthesisToken)
            : base(syntaxTree)
        {
            Expression = expression;
            DotToken = dotToken;
            IdentifierToken = identifierToken;
            OpenParenthesisToken = openParenthesisToken;
            Arguments = arguments;
            CloseParenthesisToken = closeParenthesisToken;
        }

        public override SyntaxKind Kind => SyntaxKind.MemberCallExpression;

        public ExpressionSyntax Expression { get; }
        public SyntaxToken DotToken { get; }
        public SyntaxToken IdentifierToken { get; }
        public SyntaxToken OpenParenthesisToken { get; }
        public SeparatedSyntaxList<ExpressionSyntax> Arguments { get; }
        public SyntaxToken CloseParenthesisToken { get; }
    }
}