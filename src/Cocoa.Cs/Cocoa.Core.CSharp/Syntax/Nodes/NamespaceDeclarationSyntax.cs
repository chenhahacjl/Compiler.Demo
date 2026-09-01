using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 鍛藉悕绌洪棿澹版槑锛歚namespace MyLib.Models { ... }`
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

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.NamespaceDeclaration;

        public SyntaxToken NamespaceKeyword { get; }
        public ImmutableArray<SyntaxToken> NameTokens { get; }
        public SyntaxToken OpenBraceToken { get; }
        public ImmutableArray<MemberSyntax> Members { get; }
        public SyntaxToken CloseBraceToken { get; }

        public string Name => string.Concat(NameTokens.Select(t => t.Text));

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            foreach (var child in Modifiers)
            {
                yield return child;
            }
            yield return NamespaceKeyword;
            foreach (var child in NameTokens)
            {
                yield return child;
            }
            yield return OpenBraceToken;
            foreach (var child in Members)
            {
                yield return child;
            }
            yield return CloseBraceToken;
        }
    }
}

