using System.Collections.Generic;
using Cocoa.CodeAnalysis.Syntax.Green;
using System.Collections.Immutable;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax.Nodes
{
    /// <summary>
    /// 列表模式（C# 11）：`expr is [1, 2, 3]` / `expr is [1, .., 3]` / `expr is []` / `expr is [..]`。
    /// 元素为 PatternSyntax（常量/声明等）或 RangeExpressionSyntax（`..` / `..rest` 切片标记）。
    /// 空列表 `[]` 匹配长度 0；`..` 表示"零或多个任意元素"（可附声明变量名做切片绑定，暂缓）。
    /// </summary>
    public sealed class ListPatternSyntax : PatternSyntax
    {
        public ListPatternSyntax(SyntaxTree syntaxTree, SyntaxToken openBracketToken, ImmutableArray<SyntaxNode> elements, SyntaxToken closeBracketToken)
            : base(syntaxTree)
        {
            OpenBracketToken = openBracketToken;
            Elements = elements;
            CloseBracketToken = closeBracketToken;
        }

        public override SyntaxKind Kind => SyntaxKind.ListPattern;

        public SyntaxToken OpenBracketToken { get; }
        public ImmutableArray<SyntaxNode> Elements { get; }
        public SyntaxToken CloseBracketToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return OpenBracketToken;
            foreach (var element in Elements)
                yield return element;
            yield return CloseBracketToken;
        }
    }
}