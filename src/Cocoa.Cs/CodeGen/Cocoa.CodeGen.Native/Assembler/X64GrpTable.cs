using System.Collections.Generic;

namespace Cocoa.CodeGen.Native.Assembler.X64
{
    /// <summary>分组编码指令表（P2 扩展：单操作数 + 移位）。</summary>
    public static class X64GrpTable
    {
        public static readonly X64GrpEncoding Not = new("NOT", 0x02, 0xFF, 0xFF);
        public static readonly X64GrpEncoding Neg = new("NEG", 0x03, 0xFF, 0xFF);
        public static readonly X64GrpEncoding Mul = new("MUL", 0x04, 0xFF, 0xFF);
        public static readonly X64GrpEncoding Div = new("DIV", 0x06, 0xFF, 0xFF);
        public static readonly X64GrpEncoding Idiv = new("IDIV", 0x07, 0xFF, 0xFF);
        public static readonly X64GrpEncoding Shl = new("SHL", 0xFF, 0x04, 0x04);
        public static readonly X64GrpEncoding Shr = new("SHR", 0xFF, 0x05, 0x05);
        public static readonly X64GrpEncoding Sar = new("SAR", 0xFF, 0x07, 0x07);

        public static IReadOnlyList<X64GrpEncoding> All { get; } = new[] { Not, Neg, Mul, Div, Idiv, Shl, Shr, Sar };
    }
}