namespace Cocoa.CodeAnalysis.Emit.Native.Lir
{
    /// <summary>虚拟寄存器：无上限，由后端（LirToAssembler）分配物理寄存器或栈槽；携带类型驱动宽度。</summary>
    internal sealed class LirVirtualRegister
    {
        internal LirVirtualRegister(int id, LirType type)
        {
            Id = id;
            Type = type;
        }

        public int Id { get; }

        /// <summary>寄存器类型（Phase 2 LirType）：驱动栈槽宽度与运算语义。</summary>
        public LirType Type { get; }

        public override string ToString() => "v" + Id;

        public override bool Equals(object? obj) => obj is LirVirtualRegister other && other.Id == Id;

        public override int GetHashCode() => Id;
    }

    /// <summary>虚拟寄存器分配器：顺序发放全局唯一 id。</summary>
    internal sealed class LirVirtualRegisterAllocator
    {
        private int _nextId;

        public LirVirtualRegister Allocate() => new LirVirtualRegister(_nextId++, LirType.I32);

        public LirVirtualRegister Allocate(LirType type) => new LirVirtualRegister(_nextId++, type);
    }
}