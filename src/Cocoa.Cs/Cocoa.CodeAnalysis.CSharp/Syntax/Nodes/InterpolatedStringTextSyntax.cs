using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>鎻掑€煎瓧绗︿覆涓殑瀛楅潰閲忔枃鏈锛堝悎鎴?StringToken锛孷alue 涓哄凡澶勭悊杞箟鍚庣殑鏂囨湰锛夈€?/summary>
    public sealed partial class InterpolatedStringTextSyntax : InterpolatedStringContentSyntax
    {
        internal InterpolatedStringTextSyntax(SyntaxTree syntaxTree, SyntaxToken textToken)
            : base(syntaxTree)
        {
            TextToken = textToken;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.InterpolatedStringText;

        public SyntaxToken TextToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return TextToken;
        }
    }
}

