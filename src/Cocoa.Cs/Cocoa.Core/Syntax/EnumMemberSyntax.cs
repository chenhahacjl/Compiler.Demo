namespace Cocoa.CodeAnalysis.Syntax
{
    public sealed partial class EnumMemberSyntax : SyntaxNode
    {
        internal EnumMemberSyntax(SyntaxTree syntaxTree, SyntaxToken identifier, SyntaxToken? equalsToken, ExpressionSyntax? value)
            : base(syntaxTree)
        {
            Identifier = identifier;
            EqualsToken = equalsToken;
            Value = value;
        }

        public override SyntaxKind Kind => SyntaxKind.EnumMember;

        public SyntaxToken Identifier { get; }
        public SyntaxToken? EqualsToken { get; }
        public ExpressionSyntax? Value { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Identifier;
            if (EqualsToken != null)
            {
                yield return EqualsToken;
            }
            if (Value != null)
            {
                yield return Value;
            }
        }
    }
}
