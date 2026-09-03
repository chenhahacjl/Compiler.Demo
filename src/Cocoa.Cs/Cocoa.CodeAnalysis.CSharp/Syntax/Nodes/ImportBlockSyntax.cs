using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// import 鍧楄妭鐐癸細`import kernel32.dll { static extern ... }`锛堢被鎴愬憳锛?e-M17 Step 4锛夈€?
    /// 鍧楀唴鍙厑璁?`static` extern 鍑芥暟澹版槑锛孌LL 褰掑睘鐢卞潡澹版槑寮忕粦瀹氥€?
    /// 鍙€夊潡绾?charset 閿細`import user32.dll charset = unicode`锛?e-M17 Step 5锛夈€?
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

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.ImportBlock;

        public SyntaxToken ImportKeyword { get; }

        public ImmutableArray<SyntaxToken> NameTokens { get; }

        public SyntaxToken? OpenParenthesisToken { get; }

        /// <summary>鍧楃骇 charset 閿紙`charset`锛夛紝6e-M17 Step 5锛涚己鐪?null銆?/summary>
        public SyntaxToken? CharsetKey { get; }

        /// <summary>鍧楃骇 charset 鍊硷紙`ansi` / `unicode` / `auto`锛夛紝6e-M17 Step 5锛涚己鐪?null锛? unicode锛夈€?/summary>
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

