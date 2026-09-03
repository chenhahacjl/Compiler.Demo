using System.Collections.Immutable;
using System.IO;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 语法绿节点（Phase 4：不可变、无父链、无引用，可跨树共享；对应 Roslyn
    /// <see cref="Microsoft.CodeAnalysis.Syntax.InternalSyntax.GreenNode"/>）。
    /// 红树（<see cref="SyntaxNode"/>）可经绿节点惰性实现；当前先落地绿层 + <see cref="SyntaxFactory"/>；
    /// 解析器迁移为后续里程碑。
    /// </summary>
    public abstract class GreenNode
    {
        private protected GreenNode(int rawKind)
        {
            RawKind = rawKind;
        }

    /// <summary>语言无关的原始 kind（P1-E-1：存储层与语言枚举解耦，为拆两套 SyntaxKind 铺路）</summary>
        public int RawKind { get; }

    /// <summary>便捷视图：当前共享联合枚举（过渡态；P1-E-2 拆两套枚举后由各语言节点层提供）。</summary>
        public SyntaxKind Kind => (SyntaxKind)RawKind;

    /// <summary>文本宽度（含子节点 trivia）</summary>
        public abstract int Width { get; }

    /// <summary>直接子槽位数。</summary>
        public abstract int SlotCount { get; }

        public abstract GreenNode? GetSlot(int index);

        public abstract void WriteTo(TextWriter writer);

        public override string ToString()
        {
            using var writer = new StringWriter();
            WriteTo(writer);
            return writer.ToString();
        }

        /// <summary>绿→红（真·惰性红视图）：产出一个包裹本绿节点的 <see cref="RedNode"/>，
    /// 子节点经 <see cref="GreenNode.GetSlot"/> 惰性实现。</summary>
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
