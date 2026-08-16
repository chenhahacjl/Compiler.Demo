namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 数组索引表达式语法：a[i]
    /// </summary>
    public sealed partial class ElementAccessExpressionSyntax : ExpressionSyntax
    {
        internal ElementAccessExpressionSyntax(SyntaxTree syntaxTree, ExpressionSyntax expression, SyntaxToken openBracketToken, ExpressionSyntax index, SyntaxToken closeBracketToken)
            : base(syntaxTree)
        {
            Expression = expression;
            OpenBracketToken = openBracketToken;
            Index = index;
            CloseBracketToken = closeBracketToken;
        }

        public override SyntaxKind Kind => SyntaxKind.ElementAccessExpression;

        public ExpressionSyntax Expression { get; }
        public SyntaxToken OpenBracketToken { get; }
        public ExpressionSyntax Index { get; }
        public SyntaxToken CloseBracketToken { get; }
    }
}