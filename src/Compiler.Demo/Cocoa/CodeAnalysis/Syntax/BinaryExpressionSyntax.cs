using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 二元表达式语法
    /// </summary>
    public sealed class BinaryExpressionSyntax : ExpressionSyntax
    {
        public BinaryExpressionSyntax(ExpressionSyntax left, SyntaxToken operationToken, ExpressionSyntax right)
        {
            Left = left;
            OperationToken = operationToken;
            Right = right;
        }

        public override SyntaxKind Kind => SyntaxKind.BinaryExpression;

        public ExpressionSyntax Left { get; }
        public SyntaxToken OperationToken { get; }
        public ExpressionSyntax Right { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Left;
            yield return OperationToken;
            yield return Right;
        }
    }
}
