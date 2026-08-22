using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// import 块节点：`import kernel32.dll { static extern ... }`（类成员，6e-M17 Step 4）。
    /// 块内只允许 `static` extern 函数声明，DLL 归属由块声明式绑定。
    /// </summary>
    public sealed partial class ImportBlockSyntax : MemberSyntax
    {
        internal ImportBlockSyntax(SyntaxTree syntaxTree, SyntaxToken importKeyword, ImmutableArray<SyntaxToken> nameTokens, SyntaxToken openBraceToken, ImmutableArray<MemberSyntax> members, SyntaxToken closeBraceToken)
            : base(syntaxTree, ImmutableArray<SyntaxToken>.Empty)
        {
            ImportKeyword = importKeyword;
            NameTokens = nameTokens;
            OpenBraceToken = openBraceToken;
            Members = members;
            CloseBraceToken = closeBraceToken;
        }

        public override SyntaxKind Kind => SyntaxKind.ImportBlock;

        public SyntaxToken ImportKeyword { get; }

        public ImmutableArray<SyntaxToken> NameTokens { get; }

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
    }
}
