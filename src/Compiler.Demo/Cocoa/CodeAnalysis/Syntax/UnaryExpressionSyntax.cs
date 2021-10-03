using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 一元表达式语法
    /// </summary>
    public sealed class UnaryExpressionSyntax : ExpressionSyntax
    {
        public UnaryExpressionSyntax(SyntaxToken operationToken, ExpressionSyntax operand)
        {
            OperationToken = operationToken;
            Operand = operand;
        }

        public override SyntaxKind Kind => SyntaxKind.UnaryExpression;

        public SyntaxToken OperationToken { get; }
        public ExpressionSyntax Operand { get; }
    }
}
