namespace Cocoa.CodeAnalysis.Syntax
{
    public sealed partial class EnumDeclarationSyntax : MemberSyntax
    {
        internal EnumDeclarationSyntax(SyntaxTree syntaxTree, SyntaxToken? publicKeyword, SyntaxToken enumKeyword, SyntaxToken identifier, SyntaxToken openBraceToken, SeparatedSyntaxList<EnumMemberSyntax> members, SyntaxToken closeBraceToken)
            : base(syntaxTree)
        {
            PublicKeyword = publicKeyword;
            EnumKeyword = enumKeyword;
            Identifier = identifier;
            OpenBraceToken = openBraceToken;
            Members = members;
            CloseBraceToken = closeBraceToken;
        }

        public override SyntaxKind Kind => SyntaxKind.EnumDeclaration;

        public SyntaxToken? PublicKeyword { get; }
        public SyntaxToken EnumKeyword { get; }
        public SyntaxToken Identifier { get; }
        public SyntaxToken OpenBraceToken { get; }
        public SeparatedSyntaxList<EnumMemberSyntax> Members { get; }
        public SyntaxToken CloseBraceToken { get; }
    }
}
