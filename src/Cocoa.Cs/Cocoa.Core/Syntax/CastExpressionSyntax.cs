namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 绫诲瀷杞崲琛ㄨ揪寮忚娉?( type ) expr
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

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return OpenParenthesisToken;
            yield return TypeName;
            yield return CloseParenthesisToken;
            yield return Expression;
        }
    }
}
