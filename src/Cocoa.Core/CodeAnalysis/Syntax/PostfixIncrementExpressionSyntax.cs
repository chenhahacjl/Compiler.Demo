namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 后缀自增/自减表达式 `x++` / `x--`
    /// </summary>
    public sealed partial class PostfixIncrementExpressionSyntax : ExpressionSyntax
    {
        internal PostfixIncrementExpressionSyntax(SyntaxTree syntaxTree, ExpressionSyntax operand, SyntaxToken operatorToken)
            : base(syntaxTree)
        {
            Operand = operand;
            OperatorToken = operatorToken;
        }

        public override SyntaxKind Kind => SyntaxKind.PostfixIncrementExpression;

        public ExpressionSyntax Operand { get; }
        public SyntaxToken OperatorToken { get; }
    }
}
