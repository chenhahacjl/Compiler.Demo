namespace Cocoa.CodeGen.Native.Assembler.X64
{
    /// <summary>
    /// 分组编码指令条目（单操作数 F6/F7 组与移位 C0/C1（imm8）、D2/D3（CL）组）：
    /// 编码各以 ModRM.reg 作为分组扩展码（Intel "group" 指令），对照 LLVM X86InstrShiftRotate.td /
    /// X86InstrMisc.td（NOT/NEG/DIV/IDIV/MUL 为 F6/F7 /digit；SHL/SHR/SAR 支持 C0/C1 imm8 与 D2/D3 CL）。
    /// </summary>
    public readonly struct X64GrpEncoding
    {
        public X64GrpEncoding(string name, byte f7, byte c1, byte d3)
        {
            Name = name;
            F7 = f7;   // F6/F7 组 digit（NOT=2 NEG=3 MUL=4 DIV=6 IDIV=7；0xFF=无）
            C1 = c1;   // C0/C1 组 digit（SHL=4 SHR=5 SAR=7；imm8）
            D3 = d3;   // D2/D3 组 digit（SHL=4 SHR=5 SAR=7；CL）
        }

        public string Name { get; }
        public byte F7 { get; }
        public byte C1 { get; }
        public byte D3 { get; }
        public bool HasF7 => F7 != 0xFF;
        public bool HasShiftImm => C1 != 0xFF;
        public bool HasShiftCl => D3 != 0xFF;

        public override string ToString() => $"{Name} F7:/{F7} C1:/{C1} D3:/{D3}";
    }
}