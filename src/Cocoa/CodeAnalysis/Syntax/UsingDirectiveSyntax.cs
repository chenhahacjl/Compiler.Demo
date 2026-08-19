using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// using 导入：`using MyLib`
    /// </summary>
    public sealed partial class UsingDirectiveSyntax : MemberSyntax
    {
        internal UsingDirectiveSyntax(SyntaxTree syntaxTree, SyntaxToken usingKeyword, ImmutableArray<SyntaxToken> nameTokens)
            : base(syntaxTree, ImmutableArray<SyntaxToken>.Empty)
        {
            UsingKeyword = usingKeyword;
            NameTokens = nameTokens;
        }

        public override SyntaxKind Kind => SyntaxKind.UsingDirective;

        public SyntaxToken UsingKeyword { get; }
        public ImmutableArray<SyntaxToken> NameTokens { get; }

        public string Name => string.Concat(NameTokens.Select(t => t.Text));
    }
}
