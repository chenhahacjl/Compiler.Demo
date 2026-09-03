using System;
using System.Buffers.Binary;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_TLS_DIRECTORY64 — TLS 目录（40 字节）。</summary>
    internal readonly record struct ImageTlsDirectory64(
        ulong StartAddressOfRawData,
        ulong EndAddressOfRawData,
        ulong AddressOfIndex,
        ulong AddressOfCallBacks,
        uint SizeOfZeroFill,
        uint Characteristics)
    {
        public static int SizeOfEntry => 40;

        public static ImageTlsDirectory64 Read(ReadOnlySpan<byte> s)
        {
            return new ImageTlsDirectory64(
                BinaryPrimitives.ReadUInt64LittleEndian(s),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(8)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(16)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(24)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(32)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(36)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(d, StartAddressOfRawData);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(8), EndAddressOfRawData);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(16), AddressOfIndex);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(24), AddressOfCallBacks);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(32), SizeOfZeroFill);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(36), Characteristics);
        }
    }
}