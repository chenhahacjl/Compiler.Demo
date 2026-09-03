using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// import 块节点：`import kernel32.dll { static extern ... }`（类成员，6e-M17 Step 4）。
    /// 块内只允许 `static` extern 函数声明，DLL 归属由块声明式绑定。
    /// 可选块级 charset 键：`import user32.dll charset = unicode`（6e-M17 Step 5）。
    /// </summary>
    public sealed partial class ImportBlockSyntax : MemberSyntax
    {
        internal ImportBlockSyntax(SyntaxTree syntaxTree, SyntaxToken importKeyword, ImmutableArray<SyntaxToken> nameTokens, SyntaxToken? openParenthesisToken, SyntaxToken? charsetKey, SyntaxToken? charsetValue, SyntaxToken? closeParenthesisToken, SyntaxToken openBraceToken, ImmutableArray<MemberSyntax> members, SyntaxToken closeBraceToken)
            : base(syntaxTree, ImmutableArray<SyntaxToken>.Empty)
        {
            ImportKeyword = importKeyword;
            NameTokens = nameTokens;
            OpenParenthesisToken = openParenthesisToken;
            CharsetKey = charsetKey;
            CharsetValue = charsetValue;
            CloseParenthesisToken = closeParenthesisToken;
            OpenBraceToken = openBraceToken;
            Members = members;
            CloseBraceToken = closeBraceToken;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.ImportBlock;

        public SyntaxToken ImportKeyword { get; }

        public ImmutableArray<SyntaxToken> NameTokens { get; }

        public SyntaxToken? OpenParenthesisToken { get; }

        /// <summary>块级 charset 键（`charset`），6e-M17 Step 5；缺失为 null。</summary>
        public SyntaxToken? CharsetKey { get; }

        /// <summary>块级 charset 值（`ansi` / `unicode` / `auto`），6e-M17 Step 5；缺失为 null（默认 unicode）。</summary>
        public SyntaxToken? CharsetValue { get; }

        public SyntaxToken? CloseParenthesisToken { get; }

        public SyntaxToken OpenBraceToken { get; }

        public ImmutableArray<MemberSyntax> Members { get; }

        public SyntaxToken CloseBraceToken { get; }

        public string DllName
        {
            get
            {
                return string.Concat(NameTokens.Select(t => t.Text));
            }
        }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            foreach (var child in Modifiers)
            {
                yield return child;
            }
            yield return ImportKeyword;
            foreach (var child in NameTokens)
            {
                yield return child;
            }
            if (OpenParenthesisToken != null)
            {
                yield return OpenParenthesisToken;
            }
            if (CharsetKey != null)
            {
                yield return CharsetKey;
            }
            if (CharsetValue != null)
            {
                yield return CharsetValue;
            }
            if (CloseParenthesisToken != null)
            {
                yield return CloseParenthesisToken;
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

