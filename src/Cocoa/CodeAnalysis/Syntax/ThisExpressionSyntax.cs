namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// this 表达式：`this._x` / `this.Method()`（显式实例引用）。
    /// </summary>
    public sealed partial class ThisExpressionSyntax : ExpressionSyntax
    {
        internal ThisExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken thisKeyword)
            : base(syntaxTree)
        {
            ThisKeyword = thisKeyword;
        }

        public override SyntaxKind Kind => SyntaxKind.ThisExpression;

        public SyntaxToken ThisKeyword { get; }
    }
}
