using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    public sealed partial class EnumDeclarationSyntax : MemberSyntax
    {
        internal EnumDeclarationSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers, SyntaxToken enumKeyword, SyntaxToken identifier, SyntaxToken openBraceToken, SeparatedSyntaxList<EnumMemberSyntax> members, SyntaxToken closeBraceToken)
            : base(syntaxTree, modifiers)
        {
            EnumKeyword = enumKeyword;
            Identifier = identifier;
            OpenBraceToken = openBraceToken;
            Members = members;
            CloseBraceToken = closeBraceToken;
        }

        public override SyntaxKind Kind => SyntaxKind.EnumDeclaration;

        public SyntaxToken EnumKeyword { get; }
        public SyntaxToken Identifier { get; }
        public SyntaxToken OpenBraceToken { get; }
        public SeparatedSyntaxList<EnumMemberSyntax> Members { get; }
        public SyntaxToken CloseBraceToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            foreach (var child in Modifiers)
            {
                yield return child;
            }
            yield return EnumKeyword;
            yield return Identifier;
            yield return OpenBraceToken;
            foreach (var child in Members.GetWithSeparators())
            {
                yield return child;
            }
            yield return CloseBraceToken;
        }
    }
}
