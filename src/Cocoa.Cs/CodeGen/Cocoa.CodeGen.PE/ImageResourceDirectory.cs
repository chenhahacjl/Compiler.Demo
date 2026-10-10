using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_RESOURCE_DIRECTORY — 资源目录（16 字节）。</summary>
    public readonly record struct ImageResourceDirectory(
        uint Characteristics,
        uint TimeDateStamp,
        ushort MajorVersion,
        ushort MinorVersion,
        ushort NumberOfNamedEntries,
        ushort NumberOfIdEntries)
    {
        public static int SizeOfEntry => 16;

        public int EntryCount => NumberOfNamedEntries + NumberOfIdEntries;

        public static ImageResourceDirectory Read(ReadOnlySpan<byte> s)
        {
            return new ImageResourceDirectory(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(8)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(10)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(12)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(14)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, Characteristics);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), TimeDateStamp);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(8), MajorVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(10), MinorVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(12), NumberOfNamedEntries);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(14), NumberOfIdEntries);
        }
    }
}
