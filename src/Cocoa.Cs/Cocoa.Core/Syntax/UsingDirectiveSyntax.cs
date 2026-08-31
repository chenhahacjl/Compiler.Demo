using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// using 导入（6e-M18）：`using MyLib` / `using static MyClass` / `using Alias = MyLib`
    /// </summary>
    public sealed partial class UsingDirectiveSyntax : MemberSyntax
    {
        internal UsingDirectiveSyntax(SyntaxTree syntaxTree, SyntaxToken usingKeyword, SyntaxToken? staticKeyword, SyntaxToken? aliasToken, SyntaxToken? equalsToken, ImmutableArray<SyntaxToken> nameTokens)
            : base(syntaxTree, ImmutableArray<SyntaxToken>.Empty)
        {
            UsingKeyword = usingKeyword;
            StaticKeyword = staticKeyword;
            AliasToken = aliasToken;
            EqualsToken = equalsToken;
            NameTokens = nameTokens;
        }

        public override SyntaxKind Kind => SyntaxKind.UsingDirective;

        public SyntaxToken UsingKeyword { get; }
        public SyntaxToken? StaticKeyword { get; }
        public SyntaxToken? AliasToken { get; }

        /// <summary>别名导入的 `=` 记号（`using Alias = Foo.Bar`；P0 起保留，绿往返完整）。</summary>
        public SyntaxToken? EqualsToken { get; }
        public ImmutableArray<SyntaxToken> NameTokens { get; }

        public string Name => string.Concat(NameTokens.Select(t => t.Text));
        public string Alias => AliasToken?.Text ?? "";
    }
}
