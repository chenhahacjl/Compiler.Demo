using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// using 导入（6e-M18）：`using MyLib` / `using static MyClass` / `using Alias = MyLib`
    /// </summary>
    public sealed partial class UsingDirectiveSyntax : MemberSyntax
    {
        internal UsingDirectiveSyntax(SyntaxTree syntaxTree, SyntaxToken usingKeyword, SyntaxToken? staticKeyword, SyntaxToken? aliasToken, SyntaxToken? equalsToken, ImmutableArray<SyntaxToken> nameTokens, ImmutableArray<SyntaxToken> modifiers = default)
            : base(syntaxTree, modifiers.IsDefault ? ImmutableArray<SyntaxToken>.Empty : modifiers)
        {
            UsingKeyword = usingKeyword;
            StaticKeyword = staticKeyword;
            AliasToken = aliasToken;
            EqualsToken = equalsToken;
            NameTokens = nameTokens;
        }

        public override SyntaxKind Kind => SyntaxKind.UsingDirective;

        /// <summary>global using（C# 10）：编译范围内生效。本编译器全局作用域已跨文件聚合 using，语义等价普通顶层 using。</summary>
        public bool IsGlobal => Modifiers.Any(m => m.Kind == SyntaxKind.GlobalKeyword);

        public SyntaxToken UsingKeyword { get; }
        public SyntaxToken? StaticKeyword { get; }
        public SyntaxToken? AliasToken { get; }

        /// <summary>别名导入的 `=` 记号（`using Alias = Foo.Bar`；P0 起保留，绿往返完整）。</summary>
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

