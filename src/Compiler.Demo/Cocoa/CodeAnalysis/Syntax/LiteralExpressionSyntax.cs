using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 文字表达式语法
    /// </summary>
    public sealed class LiteralExpressionSyntax : ExpressionSyntax
    {
        public LiteralExpressionSyntax(SyntaxToken literalToken) :
            this(literalToken, literalToken.Value)
        {
        }

        public LiteralExpressionSyntax(SyntaxToken literalToken, object value)
        {
            LiteralToken = literalToken;
            Value = value;
        }

        public override SyntaxKind Kind => SyntaxKind.LiteralExpression;

        public SyntaxToken LiteralToken { get; }
        public object Value { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return LiteralToken;
        }
    }
}
