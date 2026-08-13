namespace Cocoa.CodeAnalysis.Emit.IR
{
    /// <summary>虚拟寄存器：无上限，由后端（IrToAssembler）分配物理寄存器或栈槽。</summary>
    internal sealed class IrVirtualRegister
    {
        internal IrVirtualRegister(int id)
        {
            Id = id;
        }

        public int Id { get; }

        public override string ToString() => "v" + Id;

        public override bool Equals(object? obj) => obj is IrVirtualRegister other && other.Id == Id;

        public override int GetHashCode() => Id;
    }

    /// <summary>虚拟寄存器分配器：顺序发放全局唯一 id。</summary>
    internal sealed class IrVirtualRegisterAllocator
    {
        private int _nextId;

        public IrVirtualRegister Allocate() => new IrVirtualRegister(_nextId++);
    }
}
