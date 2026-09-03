using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 类型转换表达式语法 ( type ) expr
    /// </summary>
    public sealed partial class CastExpressionSyntax : ExpressionSyntax
    {
        internal CastExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken openParenthesisToken, SyntaxToken typeName, SyntaxToken closeParenthesisToken, ExpressionSyntax expression)
            : base(syntaxTree)
        {
            OpenParenthesisToken = openParenthesisToken;
            TypeName = typeName;
            CloseParenthesisToken = closeParenthesisToken;
            Expression = expression;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.CastExpression;

        public SyntaxToken OpenParenthesisToken { get; }
        public SyntaxToken TypeName { get; }
        public SyntaxToken CloseParenthesisToken { get; }
        public ExpressionSyntax Expression { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return OpenParenthesisToken;
            yield return TypeName;
            yield return CloseParenthesisToken;
            yield return Expression;
        }
    }
}

