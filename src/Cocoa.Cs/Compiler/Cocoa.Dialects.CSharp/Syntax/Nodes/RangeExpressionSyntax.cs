using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 范围表达式：start..end / start.. / ..end / ..
    /// </summary>
    public sealed partial class RangeExpressionSyntax : ExpressionSyntax
    {
        internal RangeExpressionSyntax(SyntaxTree syntaxTree, ExpressionSyntax? left, SyntaxToken dotDotToken, ExpressionSyntax? right)
            : base(syntaxTree)
        {
            Left = left;
            DotDotToken = dotDotToken;
            Right = right;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.RangeExpression;

        public ExpressionSyntax? Left { get; }
        public SyntaxToken DotDotToken { get; }
        public ExpressionSyntax? Right { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            if (Left != null) yield return Left;
            yield return DotDotToken;
            if (Right != null) yield return Right;
        }
    }
}
