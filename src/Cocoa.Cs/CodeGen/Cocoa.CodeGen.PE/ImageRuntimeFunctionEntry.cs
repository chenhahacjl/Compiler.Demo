using System;
using System.Buffers.Binary;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_RUNTIME_FUNCTION_ENTRY — 异常处理函数表项（12 字节）。</summary>
    public readonly record struct ImageRuntimeFunctionEntry(
        uint BeginAddress,
        uint EndAddress,
        uint UnwindInfoAddress)
    {
        public static int SizeOfEntry => 12;

        public static ImageRuntimeFunctionEntry Read(ReadOnlySpan<byte> s)
        {
            return new ImageRuntimeFunctionEntry(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, BeginAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), EndAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(8), UnwindInfoAddress);
        }
    }
}
