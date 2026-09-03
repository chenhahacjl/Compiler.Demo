using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 鏂囧瓧琛ㄨ揪寮忚娉?
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

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.LiteralExpression;

        public SyntaxToken LiteralToken { get; }
        public object Value { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return LiteralToken;
        }
    }
}

