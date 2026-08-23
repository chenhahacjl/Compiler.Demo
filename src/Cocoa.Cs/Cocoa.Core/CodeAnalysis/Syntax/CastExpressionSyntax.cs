namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 类型转换表达式语法 ( type ) expr
    /// </summary>
    public sealed partial class CastExpressionSyntax : ExpressionSyntax
    {
        internal CastExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken openParenthesisToken, SyntaxToken typeName, SyntaxToken closeParenthesisToken, ExpressionSyntax expression)
            : base(syntaxTree)
        {
            OpenParenthesisToken = openParenthesisToken;
            TypeName = typeName;
            CloseParenthesisToken = closeParenthesisToken;
            Expression = expression;
        }

        public override SyntaxKind Kind => SyntaxKind.CastExpression;

        public SyntaxToken OpenParenthesisToken { get; }
        public SyntaxToken TypeName { get; }
        public SyntaxToken CloseParenthesisToken { get; }
        public ExpressionSyntax Expression { get; }
    }
}
