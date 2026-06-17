namespace Cocoa.CodeAnalysis.Syntax
{
    public sealed partial class PostfixUnaryExpressionSyntax : ExpressionSyntax
    {
        internal PostfixUnaryExpressionSyntax(
            SyntaxTree syntaxTree,
            ExpressionSyntax operand,
            SyntaxToken operatorToken)
            : base(syntaxTree)
        {
            Operand = operand;
            OperatorToken = operatorToken;
        }

        public override SyntaxKind Kind => SyntaxKind.PostfixUnaryExpression;
        public ExpressionSyntax Operand { get; }
        public SyntaxToken OperatorToken { get; }
    }
}
