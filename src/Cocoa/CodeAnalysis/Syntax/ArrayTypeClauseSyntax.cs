namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 数组类型语法（int[] / int[][]，ElementType 递归嵌套）
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

        public override SyntaxKind Kind => SyntaxKind.ArrayTypeClause;

        public TypeClauseSyntax ElementType { get; }
        public SyntaxToken OpenBracketToken { get; }
        public SyntaxToken CloseBracketToken { get; }
    }
}