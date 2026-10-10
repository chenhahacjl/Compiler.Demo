namespace Cocoa.CodeGen.Native.Assembler.X64
{
    /// <summary>
    /// SSE 标量编码条目（0F 前缀组）。对照 Intel SDM（MOVSD/ADDSD… 66/F2/F3 + 0F）；
    /// LLVM X86InstrSSE.td（MOVSDrm=0F10 F2、MOVSDmr=0F11 F2、ADDSDrr=0F58 F2、
    /// CVTSI2SDrr=0F2A F2、CVTTSD2SIrr=0F2C F2、UCOMISDrr=0F2E 66、CVTSS2SDrr=0F5A F3、CVTSD2SSrr=0F5A F2）。
    /// Op 为 0F 之后字节；Store 时 OpStore=Op+1（mov 系 0x10/0x11）。
    /// </summary>
    public readonly struct SseEncoding
    {
        public SseEncoding(string name, byte prefix, byte op, bool store = false, bool rexW = false, bool imm = false, bool regSwap = false)
        {
            Name = name;
            Prefix = prefix;
            Op = op;
            Store = store;
            RexW = rexW;
            Imm = imm;
            RegSwap = regSwap;
        }

        public string Name { get; }
        public byte Prefix { get; }
        public byte Op { get; }
        public byte OpStore => (byte)(Op + (Store ? 1 : 0));
        public bool Store { get; }
        public bool RexW { get; }
        public bool Imm { get; }
        public bool RegSwap { get; }

        public override string ToString() => $"{Name} {Prefix:X2} 0F {Op:X2}{(Store ? " store" : "")}{(RexW ? " rexW" : "")}";
    }
}