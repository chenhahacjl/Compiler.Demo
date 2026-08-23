using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 命名空间声明：`namespace MyLib.Models { ... }`
    /// </summary>
    public sealed partial class NamespaceDeclarationSyntax : MemberSyntax
    {
        internal NamespaceDeclarationSyntax(SyntaxTree syntaxTree, SyntaxToken namespaceKeyword, ImmutableArray<SyntaxToken> nameTokens, SyntaxToken openBraceToken, ImmutableArray<MemberSyntax> members, SyntaxToken closeBraceToken)
            : base(syntaxTree, ImmutableArray<SyntaxToken>.Empty)
        {
            NamespaceKeyword = namespaceKeyword;
            NameTokens = nameTokens;
            OpenBraceToken = openBraceToken;
            Members = members;
            CloseBraceToken = closeBraceToken;
        }

        public override SyntaxKind Kind => SyntaxKind.NamespaceDeclaration;

        public SyntaxToken NamespaceKeyword { get; }
        public ImmutableArray<SyntaxToken> NameTokens { get; }
        public SyntaxToken OpenBraceToken { get; }
        public ImmutableArray<MemberSyntax> Members { get; }
        public SyntaxToken CloseBraceToken { get; }

        public string Name => string.Concat(NameTokens.Select(t => t.Text));
    }
}
