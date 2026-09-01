using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 鏁扮粍绱㈠紩琛ㄨ揪寮忚娉曪細a[i]
    /// </summary>
    public sealed partial class ElementAccessExpressionSyntax : ExpressionSyntax
    {
        internal ElementAccessExpressionSyntax(SyntaxTree syntaxTree, ExpressionSyntax expression, SyntaxToken openBracketToken, ExpressionSyntax index, SyntaxToken closeBracketToken)
            : base(syntaxTree)
        {
            Expression = expression;
            OpenBracketToken = openBracketToken;
            Index = index;
            CloseBracketToken = closeBracketToken;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.ElementAccessExpression;

        public ExpressionSyntax Expression { get; }
        public SyntaxToken OpenBracketToken { get; }
        public ExpressionSyntax Index { get; }
        public SyntaxToken CloseBracketToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Expression;
            yield return OpenBracketToken;
            yield return Index;
            yield return CloseBracketToken;
        }
    }
}

