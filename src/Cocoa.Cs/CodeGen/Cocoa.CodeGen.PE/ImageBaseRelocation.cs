using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_BASE_RELOCATION — 基址重定位块（8 字节头 + TypeOffset 数组）。</summary>
    public readonly record struct ImageBaseRelocation(uint VirtualAddress, uint SizeOfBlock)
    {
        public static int SizeOfEntry => 8;

        public int RelocationCount => (int)((SizeOfBlock - SizeOfEntry) / 2);

        public static ImageBaseRelocation Read(ReadOnlySpan<byte> s)
        {
            return new ImageBaseRelocation(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, VirtualAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), SizeOfBlock);
        }
    }
}
