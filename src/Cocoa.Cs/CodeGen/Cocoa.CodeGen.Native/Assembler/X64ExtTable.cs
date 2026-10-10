using System.Collections.Generic;

namespace Cocoa.CodeGen.Native.Assembler.X64
{
    /// <summary>扩展指令表（P3：IMUL / MOVZX / MOVSXD）。</summary>
    public static class X64ExtTable
    {
        public static readonly X64ExtEncoding Imul = new("IMUL", 0xAF, twoByte: true);
        public static readonly X64ExtEncoding MovzxB = new("MOVZX", 0xB6, twoByte: true);
        public static readonly X64ExtEncoding MovzxW = new("MOVZX", 0xB7, twoByte: true);
        public static readonly X64ExtEncoding Movsxd = new("MOVSXD", 0x63, twoByte: false, forceRexW: true);

        public static IReadOnlyList<X64ExtEncoding> All { get; } = new[] { Imul, MovzxB, MovzxW, Movsxd };
    }
}