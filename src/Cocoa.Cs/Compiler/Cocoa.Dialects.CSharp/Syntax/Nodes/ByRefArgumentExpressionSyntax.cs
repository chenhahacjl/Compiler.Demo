using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// byref 实参表达式：`out x` / `ref arr[i]`（6e-M23 R1；仅调用实参位合法，绑定层校验）。
    /// </summary>
    public sealed partial class ByRefArgumentExpressionSyntax : ExpressionSyntax
    {
        internal ByRefArgumentExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken keyword, ExpressionSyntax expression)
            : base(syntaxTree)
        {
            Keyword = keyword;
            Expression = expression;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.ByRefArgument;

        public SyntaxToken Keyword { get; }
        public bool IsRef => Keyword.Kind == (SyntaxKind)CSharpSyntaxKind.RefKeyword;

        public ExpressionSyntax Expression { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Keyword;
            yield return Expression;
        }
    }
}


