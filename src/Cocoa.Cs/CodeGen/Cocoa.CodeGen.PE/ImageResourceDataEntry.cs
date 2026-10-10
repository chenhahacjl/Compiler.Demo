using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_RESOURCE_DATA_ENTRY — 资源数据项（16 字节）。</summary>
    public readonly record struct ImageResourceDataEntry(
        uint OffsetToData,
        uint Size,
        uint CodePage,
        uint Reserved)
    {
        public static int SizeOfEntry => 16;

        public static ImageResourceDataEntry Read(ReadOnlySpan<byte> s)
        {
            return new ImageResourceDataEntry(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(12)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, OffsetToData);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), Size);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(8), CodePage);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(12), Reserved);
        }
    }
}
