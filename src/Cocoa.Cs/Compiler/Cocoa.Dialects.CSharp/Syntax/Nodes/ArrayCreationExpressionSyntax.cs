using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
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

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.ArrayCreationExpression;

        public SyntaxToken NewKeyword { get; }
        public SyntaxToken Identifier { get; }
        public SyntaxToken OpenBracketToken { get; }
        public ExpressionSyntax? Size { get; }
        public SyntaxToken CloseBracketToken { get; }
        public SyntaxToken? OpenBraceToken { get; }
        public SeparatedSyntaxList<ExpressionSyntax> Elements { get; }
        public SyntaxToken? CloseBraceToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return NewKeyword;
            yield return Identifier;
            yield return OpenBracketToken;
            if (Size != null)
            {
                yield return Size;
            }
            yield return CloseBracketToken;
            if (OpenBraceToken != null)
            {
                yield return OpenBraceToken;
            }
            foreach (var child in Elements.GetWithSeparators())
            {
                yield return child;
            }
            if (CloseBraceToken != null)
            {
                yield return CloseBraceToken;
            }
        }
    }
}

