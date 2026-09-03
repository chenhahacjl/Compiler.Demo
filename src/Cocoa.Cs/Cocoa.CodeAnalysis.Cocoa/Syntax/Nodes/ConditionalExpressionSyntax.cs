using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 涓夊厓琛ㄨ揪寮?`cond ? a : b`锛堝彸缁撳悎锛?
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

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.ConditionalExpression;

        public ExpressionSyntax Condition { get; }
        public SyntaxToken QuestionToken { get; }
        public ExpressionSyntax WhenTrue { get; }
        public SyntaxToken ColonToken { get; }
        public ExpressionSyntax WhenFalse { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Condition;
            yield return QuestionToken;
            yield return WhenTrue;
            yield return ColonToken;
            yield return WhenFalse;
        }
    }
}

