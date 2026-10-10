namespace Cocoa.CodeGen.Native.Assembler.X64
{
    /// <summary>
    /// 扩展编码条目（0F 双字节 / 单字节扩展指令）。对照 Intel SDM（IMUL r, r/m = 0F AF；
    /// MOVZX r, r/m8=0F B6 / r16=0F B7；MOVSXD r64, r/m32 = 63 /r）与 LLVM X86InstrCompiler.td（IMUL64rr）
    /// / X86InstrExtension.td（MOVZX64rr32 等）。
    /// </summary>
    public readonly struct X64ExtEncoding
    {
        public X64ExtEncoding(string name, byte opLow, bool twoByte, bool forceRexW = false)
        {
            Name = name;
            OpLow = opLow;
            TwoByte = twoByte;
            ForceRexW = forceRexW;
        }

        public string Name { get; }
        public byte OpLow { get; }
        public bool TwoByte { get; }
        public bool ForceRexW { get; }

        public override string ToString() => $"{Name} {(TwoByte ? "0F " : "")}{OpLow:X2}";
    }
}