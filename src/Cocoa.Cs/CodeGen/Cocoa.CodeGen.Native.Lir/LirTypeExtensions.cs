namespace Cocoa.CodeGen.Native.Lir
{
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