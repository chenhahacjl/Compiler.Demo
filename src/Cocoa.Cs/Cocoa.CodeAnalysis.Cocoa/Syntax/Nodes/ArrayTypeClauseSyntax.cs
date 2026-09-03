using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 鏁扮粍绫诲瀷璇硶锛坕nt[] / int[][]锛孍lementType 閫掑綊宓屽锛?
    /// </summary>
    public sealed partial class ArrayTypeClauseSyntax : TypeClauseSyntax
    {
        internal ArrayTypeClauseSyntax(SyntaxTree syntaxTree, SyntaxToken? colonToken, TypeClauseSyntax elementType, SyntaxToken openBracketToken, SyntaxToken closeBracketToken)
            : base(syntaxTree, colonToken, elementType.Identifier)
        {
            ElementType = elementType;
            OpenBracketToken = openBracketToken;
            CloseBracketToken = closeBracketToken;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.ArrayTypeClause;

        public TypeClauseSyntax ElementType { get; }
        public SyntaxToken OpenBracketToken { get; }
        public SyntaxToken CloseBracketToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            if (ColonToken != null)
            {
                yield return ColonToken;
            }
            yield return Identifier;
            yield return ElementType;
            yield return OpenBracketToken;
            yield return CloseBracketToken;
        }
    }
}

