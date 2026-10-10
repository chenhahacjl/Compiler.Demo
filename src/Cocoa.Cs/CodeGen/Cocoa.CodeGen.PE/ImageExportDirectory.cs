using System;
using System.Buffers.Binary;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_EXPORT_DIRECTORY — 导出目录（40 字节）。</summary>
    public readonly record struct ImageExportDirectory(
        uint Characteristics,
        uint TimeDateStamp,
        ushort MajorVersion,
        ushort MinorVersion,
        uint Name,
        uint Base,
        uint NumberOfFunctions,
        uint NumberOfNames,
        uint AddressOfFunctions,
        uint AddressOfNames,
        uint AddressOfNameOrdinals)
    {
        public static int SizeOfEntry => 40;

        public static ImageExportDirectory Read(ReadOnlySpan<byte> s)
        {
            return new ImageExportDirectory(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(8)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(10)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(12)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(16)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(20)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(24)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(28)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(32)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(36)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, Characteristics);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), TimeDateStamp);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(8), MajorVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(10), MinorVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(12), Name);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(16), Base);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(20), NumberOfFunctions);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(24), NumberOfNames);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(28), AddressOfFunctions);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(32), AddressOfNames);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(36), AddressOfNameOrdinals);
        }
    }
}
