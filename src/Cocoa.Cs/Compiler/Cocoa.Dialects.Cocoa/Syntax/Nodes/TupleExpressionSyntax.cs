using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 元组字面量（语言后置件）：`(a, b)`。绑定为合成值类型（字段 Item1..ItemN）+ 对象创建。
    /// 复用父括号节点形态（open/dash/close + SeparatedSyntaxList），保源码往返。
    /// </summary>
    public sealed partial class TupleExpressionSyntax : ExpressionSyntax
    {
        internal TupleExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken openParenthesisToken, SeparatedSyntaxList<ExpressionSyntax> elements, SyntaxToken closeParenthesisToken)
            : base(syntaxTree)
        {
            OpenParenthesisToken = openParenthesisToken;
            Elements = elements;
            CloseParenthesisToken = closeParenthesisToken;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.TupleExpression;

        public SyntaxToken OpenParenthesisToken { get; }
        public SeparatedSyntaxList<ExpressionSyntax> Elements { get; }
        public SyntaxToken CloseParenthesisToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return OpenParenthesisToken;
            foreach (var child in Elements.GetWithSeparators())
            {
                yield return child;
            }
            yield return CloseParenthesisToken;
        }
    }
}