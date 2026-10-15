using System.Collections.Immutable;
using System.IO;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>带子节点的复合绿节点（通用实现：存槽位数组，可容纳 null 占位槽）。</summary>
    public sealed class GreenNodeWithChildren : GreenNode
    {
        private readonly ImmutableArray<GreenNode?> _slots;

        public GreenNodeWithChildren(SyntaxKind kind, ImmutableArray<GreenNode?> slots)
            : base((int)kind)
        {
            _slots = slots.IsDefault ? ImmutableArray<GreenNode?>.Empty : slots;
        }

        public override int SlotCount => _slots.Length;

        public override GreenNode? GetSlot(int index) => _slots[index];

        public override int Width
        {
            get
            {
                var width = 0;
                foreach (var slot in _slots)
                {
                    if (slot != null)
                    {
                        width += slot.Width;
                    }
                }

                return width;
            }
        }

        public override void WriteTo(TextWriter writer)
        {
            foreach (var slot in _slots)
            {
                slot?.WriteTo(writer);
            }
        }
    }
}