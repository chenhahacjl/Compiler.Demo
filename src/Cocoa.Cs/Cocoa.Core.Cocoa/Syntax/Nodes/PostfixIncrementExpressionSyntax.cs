using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 鍚庣紑鑷/鑷噺琛ㄨ揪寮?`x++` / `x--`
    /// </summary>
    public sealed partial class PostfixIncrementExpressionSyntax : ExpressionSyntax
    {
        internal PostfixIncrementExpressionSyntax(SyntaxTree syntaxTree, ExpressionSyntax operand, SyntaxToken operatorToken)
            : base(syntaxTree)
        {
            Operand = operand;
            OperatorToken = operatorToken;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.PostfixIncrementExpression;

        public ExpressionSyntax Operand { get; }
        public SyntaxToken OperatorToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Operand;
            yield return OperatorToken;
        }
    }
}

