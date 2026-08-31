using Cocoa.CodeAnalysis.Text;
using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 通用惰性红视图（Phase 4 桥接 1b 深化第一步）：包裹 <see cref="GreenNode"/>，
    /// <see cref="Kind"/> / 子节点 / 文本位置均经绿槽惰性实现。不替换现有类型化红树，
    /// 确立「红包绿」机制；逐类型迁移（BinaryExpressionSyntax 等按绿槽直构）为后续子步。
    /// 注意：父链经 <see cref="Parent"/>（红视图链）；基类 <see cref="SyntaxNode.Parent"/>（树字典）对独立红视图为 null。
    /// </summary>
    public sealed class RedNode : SyntaxNode
    {
        private readonly GreenNode _green;
        private readonly int _position;

        internal RedNode(SyntaxTree syntaxTree, GreenNode green, int position, RedNode? parent)
            : base(syntaxTree)
        {
            _green = green;
            _position = position;
            Parent = parent;
        }

        public override SyntaxKind Kind => _green.Kind;

        /// <summary>所包裹的绿节点。</summary>
        public GreenNode Green => _green;

        /// <summary>红视图父节点（经绿槽惰性链）。</summary>
        public new RedNode? Parent { get; }

        public override TextSpan Span => _green.SlotCount == 0
            ? new TextSpan(_position, _green.Width)
            : base.Span;

        public override TextSpan FullSpan => _green.SlotCount == 0
            ? new TextSpan(_position, _green.Width)
            : base.FullSpan;

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            var position = _position;
            for (var i = 0; i < _green.SlotCount; i++)
            {
                var slot = _green.GetSlot(i);
                if (slot == null)
                {
                    continue;
                }

                yield return slot.CreateRed(SyntaxTree, position, this);
                position += slot.Width;
            }
        }

        public override string ToString() => _green.ToString();
    }
}