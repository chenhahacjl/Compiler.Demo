using System;
using System.Buffers.Binary;

namespace Cocoa.CodeGen.PE
{
    /// <summary>UNWIND_CODE — 展开指令（2 字节）。</summary>
    public readonly record struct PeUnwindCode(byte CodeOffset, PeUnwindOpCode UnwindOp, byte OpInfo)
    {
        public static int SizeOfEntry => 2;

        public static PeUnwindCode Read(ReadOnlySpan<byte> s)
        {
            return new PeUnwindCode(s[0], (PeUnwindOpCode)(s[1] & 0x0F), (byte)(s[1] >> 4));
        }

        public void Write(Span<byte> d)
        {
            d[0] = CodeOffset;
            d[1] = (byte)(((int)UnwindOp & 0x0F) | (OpInfo << 4));
        }
    }
}
