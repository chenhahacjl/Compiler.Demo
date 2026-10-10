using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_RESOURCE_DIRECTORY_ENTRY — 目录项（8 字节），高位为 NameIsString / DataIsDirectory。</summary>
    public readonly record struct ImageResourceDirectoryEntry(uint Name, uint OffsetToData)
    {
        public const uint NameIsString = 0x80000000;
        public const uint DataIsDirectory = 0x80000000;

        public static int SizeOfEntry => 8;

        public bool NameIsStringFlag => (Name & NameIsString) != 0;

        public ushort NameId => unchecked((ushort)Name);

        public uint NameStringOffset => Name & ~NameIsString;

        public bool DataIsDirectoryFlag => (OffsetToData & DataIsDirectory) != 0;

        public uint OffsetToDataValue => OffsetToData & ~DataIsDirectory;

        public static ImageResourceDirectoryEntry Read(ReadOnlySpan<byte> s)
        {
            return new ImageResourceDirectoryEntry(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, Name);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), OffsetToData);
        }
    }
}
