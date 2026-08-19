namespace Cocoa.CodeAnalysis.Syntax
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

        public override SyntaxKind Kind => SyntaxKind.BaseExpression;

        public SyntaxToken BaseKeyword { get; }
    }
}
