using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 数组类型语法（int[] / int[][]，ElementType 递归嵌套）。
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

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.ArrayTypeClause;

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

