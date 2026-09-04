

namespace Cocoa.CodeGen.Native.Lir
{
    /// <summary>
    /// 虚拟寄存器类型（Phase 2 LirType）：驱动字节宽与运算语义，取代 4/8 字节裸宽。
    /// 指针（引用/数组/函数值）统一 Addr，逻辑宽 8 字节（x86 双 4 字节槽）。
    /// </summary>
    public enum LirType
    {
        /// <summary>4 字节整型（int/bool/char/u8/u16/u32/enum 窄域）。</summary>
        I32,

        /// <summary>8 字节整型（long/u64）。</summary>
        I64,

        /// <summary>4 字节浮点（float，6e-M21 Phase 5b）。</summary>
        F32,

        /// <summary>8 字节浮点（double）。</summary>
        F64,

        /// <summary>指针（引用类型/数组/函数值/字符串），逻辑宽 8 字节。</summary>
        Addr,
    }

    public static class LirTypeExtensions
    {
        /// <summary>类型字节宽：4 或 8（x86 8 字节值占双槽）。</summary>
        public static int Size(this LirType type) => type switch
        {
            LirType.I32 => 4,
            LirType.I64 => 8,
            LirType.F32 => 4,
            LirType.F64 => 8,
            LirType.Addr => 8,
            _ => throw new System.Exception($"Unknown LirType: {type}"),
        };

        /// <summary>是否 8 字节宽值（x86 双槽判定）。</summary>
        public static bool Is8Bytes(this LirType type) => type.Size() == 8;
    }
}