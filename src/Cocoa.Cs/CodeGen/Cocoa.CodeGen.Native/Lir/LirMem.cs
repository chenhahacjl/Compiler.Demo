namespace Cocoa.CodeGen.Native.Lir
{
    /// <summary>内存访问（Load/Store）专用工厂。</summary>
    public static class LirMem
    {
        public static LirInstruction Load(LirVirtualRegister dst, LirVirtualRegister baseReg, int offset, int byteSize)
        {
            return new LirInstruction(LirOpCode.Load, dst, LirOperand.Reg(baseReg), LirOperand.None, offset, byteSize);
        }

        public static LirInstruction Store(LirVirtualRegister baseReg, int offset, LirVirtualRegister src, int byteSize)
        {
            return new LirInstruction(LirOpCode.Store, null, LirOperand.Reg(baseReg), LirOperand.Reg(src), offset, byteSize);
        }
    }
}