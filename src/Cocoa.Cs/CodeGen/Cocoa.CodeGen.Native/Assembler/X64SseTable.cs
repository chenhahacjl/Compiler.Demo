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

    /// <summary>SSE 标量指令表（Scalar SIMD：double/float 算术、转换、比较、move）。</summary>
    public static class X64SseTable
    {
        // —— double（SD，前缀 F2）——
        public static readonly SseEncoding Movsd = new("MOVSD", 0xF2, 0x10, store: true);
        public static readonly SseEncoding Addsd = new("ADDSD", 0xF2, 0x58);
        public static readonly SseEncoding Subsd = new("SUBSD", 0xF2, 0x5C);
        public static readonly SseEncoding Mulsd = new("MULSD", 0xF2, 0x59);
        public static readonly SseEncoding Divsd = new("DIVSD", 0xF2, 0x5E);
        public static readonly SseEncoding Sqrtsd = new("SQRTSD", 0xF2, 0x51);
        public static readonly SseEncoding Cvtsi2sd = new("CVTSI2SD", 0xF2, 0x2A);
        public static readonly SseEncoding Cvtsi2sd64 = new("CVTSI2SD64", 0xF2, 0x2A, rexW: true);
        public static readonly SseEncoding Cvttsd2si = new("CVTTSD2SI", 0xF2, 0x2C);
        public static readonly SseEncoding Cvttsd2si64 = new("CVTTSD2SI64", 0xF2, 0x2C, rexW: true);
        public static readonly SseEncoding Cvtsd2ss = new("CVTSD2SS", 0xF2, 0x5A);

        // —— float（SS，前缀 F3）——
        public static readonly SseEncoding Movss = new("MOVSS", 0xF3, 0x10, store: true);
        public static readonly SseEncoding Addss = new("ADDSS", 0xF3, 0x58);
        public static readonly SseEncoding Subss = new("SUBSS", 0xF3, 0x5C);
        public static readonly SseEncoding Mulss = new("MULSS", 0xF3, 0x59);
        public static readonly SseEncoding Divss = new("DIVSS", 0xF3, 0x5E);
        public static readonly SseEncoding Sqrtss = new("SQRTSS", 0xF3, 0x51);
        public static readonly SseEncoding Cvtsi2ss = new("CVTSI2SS", 0xF3, 0x2A);
        public static readonly SseEncoding Cvttss2si = new("CVTTSS2SI", 0xF3, 0x2C);
        public static readonly SseEncoding Cvtss2sd = new("CVTSS2SD", 0xF3, 0x5A);

        // —— 66 前缀（SSE2 pack / 比较 / 转换）——
        public static readonly SseEncoding Ucomisd = new("UCOMISD", 0x66, 0x2E);
        public static readonly SseEncoding MovdGprToXmm = new("MOVD", 0x66, 0x6E);
        public static readonly SseEncoding MovdXmmToGpr = new("MOVD", 0x66, 0x7E, regSwap: true);
        public static readonly SseEncoding MovqGprToXmm = new("MOVQ", 0x66, 0x6E, rexW: true);
        public static readonly SseEncoding MovqXmmToGpr = new("MOVQ", 0x66, 0xD6, rexW: true, regSwap: true);
        public static readonly SseEncoding Pinsrd = new("PINSRD", 0x66, 0x22, imm: true);
        public static readonly SseEncoding Pextrd = new("PEXTRD", 0x66, 0x16, imm: true);
        public static readonly SseEncoding Roundsd = new("ROUNDSD", 0x66, 0x0B, imm: true);
        public static readonly SseEncoding Roundss = new("ROUNDSS", 0xF3, 0x0B, imm: true);

        public static readonly SseEncoding[] Scalar =
        {
            Movsd, Addsd, Subsd, Mulsd, Divsd, Sqrtsd, Cvtsi2sd, Cvtsi2sd64, Cvttsd2si, Cvttsd2si64, Cvtsd2ss,
            Movss, Addss, Subss, Mulss, Divss, Sqrtss, Cvtsi2ss, Cvttss2si, Cvtss2sd,
            Ucomisd, Roundsd, Roundss,
        };
    }
}