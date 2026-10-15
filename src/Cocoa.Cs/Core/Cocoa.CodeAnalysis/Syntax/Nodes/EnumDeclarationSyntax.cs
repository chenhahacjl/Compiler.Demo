using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    public sealed partial class EnumDeclarationSyntax : MemberSyntax
    {
        internal EnumDeclarationSyntax(SyntaxTree syntaxTree, ImmutableArray<AttributeSyntax> attributes, ImmutableArray<SyntaxToken> modifiers, SyntaxToken enumKeyword, SyntaxToken identifier, SyntaxToken openBraceToken, SeparatedSyntaxList<EnumMemberSyntax> members, SyntaxToken closeBraceToken)
            : base(syntaxTree, modifiers)
        {
            Attributes = attributes;
            EnumKeyword = enumKeyword;
            Identifier = identifier;
            OpenBraceToken = openBraceToken;
            Members = members;
            CloseBraceToken = closeBraceToken;
        }

        public override SyntaxKind Kind => SyntaxKind.EnumDeclaration;

        /// <summary>声明前 attribute 列表（6e-M32 Tier-2：枚举级）。</summary>
        public ImmutableArray<AttributeSyntax> Attributes { get; }

        public SyntaxToken EnumKeyword { get; }
        public SyntaxToken Identifier { get; }
        public SyntaxToken OpenBraceToken { get; }
        public SeparatedSyntaxList<EnumMemberSyntax> Members { get; }
        public SyntaxToken CloseBraceToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            foreach (var attribute in Attributes)
            {
                yield return attribute;
            }
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

