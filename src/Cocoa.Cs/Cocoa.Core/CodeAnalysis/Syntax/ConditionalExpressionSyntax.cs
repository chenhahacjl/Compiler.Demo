namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 三元表达式 `cond ? a : b`（右结合）
    /// </summary>
    public sealed partial class ConditionalExpressionSyntax : ExpressionSyntax
    {
        internal ConditionalExpressionSyntax(SyntaxTree syntaxTree, ExpressionSyntax condition, SyntaxToken questionToken, ExpressionSyntax whenTrue, SyntaxToken colonToken, ExpressionSyntax whenFalse)
            : base(syntaxTree)
        {
            Condition = condition;
            QuestionToken = questionToken;
            WhenTrue = whenTrue;
            ColonToken = colonToken;
            WhenFalse = whenFalse;
        }

        public override SyntaxKind Kind => SyntaxKind.ConditionalExpression;

        public ExpressionSyntax Condition { get; }
        public SyntaxToken QuestionToken { get; }
        public ExpressionSyntax WhenTrue { get; }
        public SyntaxToken ColonToken { get; }
        public ExpressionSyntax WhenFalse { get; }
    }
}
