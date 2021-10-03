using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 赋值表达式语法
    /// </summary>
    public sealed class AssignmentExpressionSyntax : ExpressionSyntax
    {
        public AssignmentExpressionSyntax(SyntaxToken identifierToken, SyntaxToken equalsToken, ExpressionSyntax expression)
        {
            IdentifierToken = identifierToken;
            EqualsToken = equalsToken;
            Expression = expression;
        }

        public override SyntaxKind Kind => SyntaxKind.AssignmentExpression;

        public SyntaxToken IdentifierToken { get; }
        public SyntaxToken EqualsToken { get; }
        public ExpressionSyntax Expression { get; }
    }
}
