using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 文字表达式语法
    /// </summary>
    public sealed partial class LiteralExpressionSyntax : ExpressionSyntax
    {
        internal LiteralExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken literalToken) :
            this(syntaxTree, literalToken, literalToken.Value!)
        {
        }

        internal LiteralExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken literalToken, object value)
            : base(syntaxTree)
        {
            LiteralToken = literalToken;
            Value = value;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.LiteralExpression;

        public SyntaxToken LiteralToken { get; }
        public object Value { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return LiteralToken;
        }
    }
}

