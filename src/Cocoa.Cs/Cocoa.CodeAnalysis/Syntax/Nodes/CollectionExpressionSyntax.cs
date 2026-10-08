using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 集合表达式：`[1, 2, 3]` / `[..a, 4]`（C# 12）。绑定阶段降级为数组创建——
    /// `..a` 为范围展开元素（spread），`a` 须为同元素类型数组。
    /// </summary>
    public sealed partial class CollectionExpressionSyntax : ExpressionSyntax
    {
        internal CollectionExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken openBracketToken, SeparatedSyntaxList<ExpressionSyntax> elements, SyntaxToken closeBracketToken)
            : base(syntaxTree)
        {
            OpenBracketToken = openBracketToken;
            Elements = elements;
            CloseBracketToken = closeBracketToken;
        }

        public override SyntaxKind Kind => SyntaxKind.CollectionExpression;

        public SyntaxToken OpenBracketToken { get; }
        public SeparatedSyntaxList<ExpressionSyntax> Elements { get; }
        public SyntaxToken CloseBracketToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return OpenBracketToken;
            foreach (var child in Elements.GetWithSeparators())
            {
                yield return child;
            }
            yield return CloseBracketToken;
        }
    }
}
