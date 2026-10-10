namespace Cocoa.CodeGen.Native.Assembler.X64
{
    /// <summary>
    /// 条件码编码：Jcc（0F 8x，条件近跳 rel32）与 Setcc（0F 9x，置位 r/m8）的第二个字节（0F 后的两字节 opcode）。
    /// 对照 Intel SDM Vol.2（Jcc 0F 8C-8F / 82-87 / 8A-8B；SETcc 0F 9C-9F / 92-97 / 9A-9B）与
    /// LLVM X86InstrInfo.td（JCC_1/JCC_4、SETCCr 由 CondCode 表驱动）。索引与 X64CondCode 枚举序一致。
    /// </summary>
    public readonly struct X64CondEncoding
    {
        public X64CondEncoding(string name, byte jcc, byte setcc)
        {
            Name = name;
            Jcc = jcc;
            Setcc = setcc;
        }

        public string Name { get; }
        public byte Jcc { get; }
        public byte Setcc { get; }

        public override string ToString() => $"{Name} jcc:0F {Jcc:X2} setcc:0F {Setcc:X2}";
    }
}