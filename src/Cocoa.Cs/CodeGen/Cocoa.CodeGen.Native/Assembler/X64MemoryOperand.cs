namespace Cocoa.CodeGen.Native.Assembler.X64
{
    public readonly struct X64MemoryOperand
    {
        public X64MemoryOperand(X64Register baseRegister, int displacement)
        {
            Base = baseRegister;
            Displacement = displacement;
        }
        public X64Register Base { get; }
        public int Displacement { get; }
    }
}