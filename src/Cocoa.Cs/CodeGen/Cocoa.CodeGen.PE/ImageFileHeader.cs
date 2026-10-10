using System;
using System.Buffers.Binary;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_FILE_HEADER — COFF 文件头（20 字节）。</summary>
    public readonly record struct ImageFileHeader(
        ushort Machine,
        ushort NumberOfSections,
        uint TimeDateStamp,
        uint PointerToSymbolTable,
        uint NumberOfSymbols,
        ushort SizeOfOptionalHeader,
        ushort Characteristics)
    {
        public static int Size => 20;

        public static ImageFileHeader Read(ReadOnlySpan<byte> s)
        {
            return new ImageFileHeader(
                BinaryPrimitives.ReadUInt16LittleEndian(s),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(2)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(12)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(16)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(18)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d, Machine);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(2), NumberOfSections);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), TimeDateStamp);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(8), PointerToSymbolTable);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(12), NumberOfSymbols);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(16), SizeOfOptionalHeader);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(18), Characteristics);
        }
    }
}
