namespace Cocoa.CodeGen.Native.Lir
{
    /// <summary>虚拟寄存器分配器：顺序发放全局唯一 id。</summary>
    public sealed class LirVirtualRegisterAllocator
    {
        private int _nextId;

        public LirVirtualRegister Allocate() => new LirVirtualRegister(_nextId++, LirType.I32);

        public LirVirtualRegister Allocate(LirType type) => new LirVirtualRegister(_nextId++, type);
    }
}