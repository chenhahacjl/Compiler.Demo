namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 数组创建表达式语法：new int[3] / new int[] {1, 2, 3}
    /// </summary>
    public sealed partial class ArrayCreationExpressionSyntax : ExpressionSyntax
    {
        internal ArrayCreationExpressionSyntax(
            SyntaxTree syntaxTree,
            SyntaxToken newKeyword,
            SyntaxToken identifier,
            SyntaxToken openBracketToken,
            ExpressionSyntax? size,
            SyntaxToken closeBracketToken,
            SyntaxToken? openBraceToken,
            SeparatedSyntaxList<ExpressionSyntax> elements,
            SyntaxToken? closeBraceToken)
            : base(syntaxTree)
        {
            NewKeyword = newKeyword;
            Identifier = identifier;
            OpenBracketToken = openBracketToken;
            Size = size;
            CloseBracketToken = closeBracketToken;
            OpenBraceToken = openBraceToken;
            Elements = elements;
            CloseBraceToken = closeBraceToken;
        }

        public override SyntaxKind Kind => SyntaxKind.ArrayCreationExpression;

        public SyntaxToken NewKeyword { get; }
        public SyntaxToken Identifier { get; }
        public SyntaxToken OpenBracketToken { get; }
        public ExpressionSyntax? Size { get; }
        public SyntaxToken CloseBracketToken { get; }
        public SyntaxToken? OpenBraceToken { get; }
        public SeparatedSyntaxList<ExpressionSyntax> Elements { get; }
        public SyntaxToken? CloseBraceToken { get; }
    }
}