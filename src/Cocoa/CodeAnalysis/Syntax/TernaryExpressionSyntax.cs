namespace Cocoa.CodeAnalysis.Syntax
{
    public sealed partial class TernaryExpressionSyntax : ExpressionSyntax
    {
        internal TernaryExpressionSyntax(
            SyntaxTree syntaxTree,
            ExpressionSyntax condition,
            SyntaxToken questionToken,
            ExpressionSyntax whenTrue,
            SyntaxToken colonToken,
            ExpressionSyntax whenFalse)
            : base(syntaxTree)
        {
            Condition = condition;
            QuestionToken = questionToken;
            WhenTrue = whenTrue;
            ColonToken = colonToken;
            WhenFalse = whenFalse;
        }

        public override SyntaxKind Kind => SyntaxKind.TernaryExpression;
        public ExpressionSyntax Condition { get; }
        public SyntaxToken QuestionToken { get; }
        public ExpressionSyntax WhenTrue { get; }
        public SyntaxToken ColonToken { get; }
        public ExpressionSyntax WhenFalse { get; }
    }
}
