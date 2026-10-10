namespace Cocoa.CodeGen.Native.Assembler.X64
{
    /// <summary>条件码编码表（P4：Jcc/Setcc 收敛为数据，供 JccOpcode/SetccOpcode 查表 + 矩阵）。</summary>
    public static class X64CondTable
    {
        public static readonly X64CondEncoding[] Items =
        {
            new("E", 0x84, 0x94),         // Equal
            new("NE", 0x85, 0x95),        // NotEqual
            new("L", 0x8C, 0x9C),         // Less
            new("LE", 0x8E, 0x9E),        // LessOrEqual
            new("G", 0x8F, 0x9F),         // Greater
            new("GE", 0x8D, 0x9D),        // GreaterOrEqual
            new("B", 0x82, 0x92),         // Below
            new("BE", 0x86, 0x96),        // BelowOrEqual
            new("A", 0x87, 0x97),         // Above
            new("AE", 0x83, 0x93),        // AboveOrEqual
            new("P", 0x8A, 0x9A),         // Parity
            new("NP", 0x8B, 0x9B),        // NoParity
        };

        public static X64CondEncoding ByCond(X64CondCode cond) => Items[(int)cond];
    }
}