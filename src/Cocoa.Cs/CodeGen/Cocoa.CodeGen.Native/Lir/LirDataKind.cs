namespace Cocoa.CodeGen.Native.Lir
{
    /// <summary>数据段项语义：Int32 / 指针（平台宽 4/8）/ UTF-16 字符串 / 原始字节 / vtable 记录（M4）。</summary>
    public enum LirDataKind
    {
        Int32,
        Pointer,
        Utf16,
        Bytes,
        VTable,
    }
}