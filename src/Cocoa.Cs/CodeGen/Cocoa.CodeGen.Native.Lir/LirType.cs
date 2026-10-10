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
}