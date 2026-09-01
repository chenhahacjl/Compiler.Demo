namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// as 绫诲瀷杞崲琛ㄨ揪寮忥紙6e-M19 M5-b锛夛細expr as TypeName 鈫?TypeName锛堝け璐ュ緱 null锛?
    /// </summary>
    public sealed partial class AsExpressionSyntax : ExpressionSyntax
    {
        internal AsExpressionSyntax(SyntaxTree syntaxTree, ExpressionSyntax expression, SyntaxToken asKeyword, SyntaxToken typeName)
            : base(syntaxTree)
        {
            Expression = expression;
            AsKeyword = asKeyword;
            TypeName = typeName;
        }

        public override SyntaxKind Kind => SyntaxKind.AsExpression;

        public ExpressionSyntax Expression { get; }
        public SyntaxToken AsKeyword { get; }
        public SyntaxToken TypeName { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Expression;
            yield return AsKeyword;
            yield return TypeName;
        }
    }
}
