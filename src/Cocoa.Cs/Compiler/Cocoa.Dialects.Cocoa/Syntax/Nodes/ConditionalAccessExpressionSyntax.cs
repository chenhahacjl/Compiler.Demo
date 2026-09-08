using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 空条件成员访问：expr?.Member / expr?.Method(args)
    /// </summary>
    public sealed partial class ConditionalAccessExpressionSyntax : ExpressionSyntax
    {
        internal ConditionalAccessExpressionSyntax(SyntaxTree syntaxTree, ExpressionSyntax expression, SyntaxToken questionDotToken, ExpressionSyntax whenNotNull)
            : base(syntaxTree)
        {
            Expression = expression;
            QuestionDotToken = questionDotToken;
            WhenNotNull = whenNotNull;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.ConditionalAccessExpression;

        public ExpressionSyntax Expression { get; }
        public SyntaxToken QuestionDotToken { get; }
        public ExpressionSyntax WhenNotNull { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Expression;
            yield return QuestionDotToken;
            yield return WhenNotNull;
        }
    }
}
