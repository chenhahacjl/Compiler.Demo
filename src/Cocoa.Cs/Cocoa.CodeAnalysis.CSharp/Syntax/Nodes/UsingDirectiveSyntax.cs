using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// using 瀵煎叆锛?e-M18锛夛細`using MyLib` / `using static MyClass` / `using Alias = MyLib`
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

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.UsingDirective;

        public SyntaxToken UsingKeyword { get; }
        public SyntaxToken? StaticKeyword { get; }
        public SyntaxToken? AliasToken { get; }

        /// <summary>鍒悕瀵煎叆鐨?`=` 璁板彿锛坄using Alias = Foo.Bar`锛汸0 璧蜂繚鐣欙紝缁垮線杩斿畬鏁达級銆?/summary>
        public SyntaxToken? EqualsToken { get; }
        public ImmutableArray<SyntaxToken> NameTokens { get; }

        public string Name => string.Concat(NameTokens.Select(t => t.Text));
        public string Alias => AliasToken?.Text ?? "";

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            foreach (var child in Modifiers)
            {
                yield return child;
            }
            yield return UsingKeyword;
            if (StaticKeyword != null)
            {
                yield return StaticKeyword;
            }
            if (AliasToken != null)
            {
                yield return AliasToken;
            }
            if (EqualsToken != null)
            {
                yield return EqualsToken;
            }
            foreach (var child in NameTokens)
            {
                yield return child;
            }
        }
    }
}

