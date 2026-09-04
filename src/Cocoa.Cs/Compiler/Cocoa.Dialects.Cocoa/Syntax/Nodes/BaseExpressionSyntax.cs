using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// base 表达式：`base.Method()`（非虚调用基类成员）。
    /// </summary>
    public sealed partial class BaseExpressionSyntax : ExpressionSyntax
    {
        internal BaseExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken baseKeyword)
            : base(syntaxTree)
        {
            BaseKeyword = baseKeyword;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.BaseExpression;

        public SyntaxToken BaseKeyword { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return BaseKeyword;
        }
    }
}

