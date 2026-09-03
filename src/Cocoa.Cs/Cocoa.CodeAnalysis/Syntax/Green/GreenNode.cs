using System.Collections.Immutable;
using System.IO;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 瑙ｆ瀽鍣ㄨ縼绉讳负鍚庣画閲岀▼纰戙€?
    /// <see cref="Microsoft.CodeAnalysis.Syntax.InternalSyntax.GreenNode"/>锛夈€?
    /// 绾㈡爲锛?see cref="SyntaxNode"/>锛夊彲缁忕豢鑺傜偣鎯版€у疄鐜帮紱褰撳墠鍏堣惤鍦扮豢灞?+ <see cref="SyntaxFactory"/>锛?
    /// 瑙ｆ瀽鍣ㄨ縼绉讳负鍚庣画閲岀▼纰戙€?
    /// </summary>
    public abstract class GreenNode
    {
        private protected GreenNode(int rawKind)
        {
            RawKind = rawKind;
        }

    /// 瑙ｆ瀽鍣ㄨ縼绉讳负鍚庣画閲岀▼纰戙€?
        public int RawKind { get; }

    /// 瑙ｆ瀽鍣ㄨ縼绉讳负鍚庣画閲岀▼纰戙€?
        public SyntaxKind Kind => (SyntaxKind)RawKind;

    /// 瑙ｆ瀽鍣ㄨ縼绉讳负鍚庣画閲岀▼纰戙€?
        public abstract int Width { get; }

    /// 瑙ｆ瀽鍣ㄨ縼绉讳负鍚庣画閲岀▼纰戙€?
        public abstract int SlotCount { get; }

        public abstract GreenNode? GetSlot(int index);

        public abstract void WriteTo(TextWriter writer);

        public override string ToString()
        {
            using var writer = new StringWriter();
            WriteTo(writer);
            return writer.ToString();
        }

        /// <summary>缁库啋绾紙鐪熉锋儼鎬х孩瑙嗗浘锛夛細浜у嚭涓€涓寘瑁规湰缁胯妭鐐圭殑 <see cref="RedNode"/>锛?
    /// 瑙ｆ瀽鍣ㄨ縼绉讳负鍚庣画閲岀▼纰戙€?
        public RedNode CreateRed(SyntaxTree syntaxTree, int position = 0, RedNode? parent = null)
        {
            return new RedNode(syntaxTree, this, position, parent);
        }


        /// <summary>绿→类型化红节点（S-5 P2-4 随迁语言库）：按 <see cref="Kind"/> 派发到具体类型；
        /// 语言库各自持有一份构建器（<c>CocoaGreenNodeFactory</c>/<c>CSharpGreenNodeFactory</c>），此处经 
        /// <see cref="Cocoa.CodeAnalysis.Language.CreateTypedRed"/> 分派；未覆盖的 Kind 回落通用 <see cref="RedNode"/>。</summary>
        public SyntaxNode CreateTypedRed(SyntaxTree syntaxTree, int position = 0)
        {
            return syntaxTree.Language.CreateTypedRed(this, syntaxTree, position);
        }
    }
}
