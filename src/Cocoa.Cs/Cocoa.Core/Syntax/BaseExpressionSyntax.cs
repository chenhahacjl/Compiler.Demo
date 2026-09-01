namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// base 琛ㄨ揪寮忥細`base.Method()`锛堥潪铏氳皟鐢ㄥ熀绫绘垚鍛橈級銆?
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

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return BaseKeyword;
        }
    }
}
