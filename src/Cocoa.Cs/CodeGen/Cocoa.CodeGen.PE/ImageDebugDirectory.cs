using System;
using System.Buffers.Binary;

namespace Cocoa.CodeGen.PE
{
    public readonly record struct ImageDebugDirectory(
        uint Characteristics,
        uint TimeDateStamp,
        ushort MajorVersion,
        ushort MinorVersion,
        PeDebugType Type,
        uint SizeOfData,
        uint AddressOfRawData,
        uint PointerToRawData)
    {
        public static int SizeOfEntry => 28;

        public static ImageDebugDirectory Read(ReadOnlySpan<byte> s)
        {
            return new ImageDebugDirectory(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(8)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(10)),
                (PeDebugType)BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(12)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(16)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(20)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(24)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, Characteristics);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), TimeDateStamp);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(8), MajorVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(10), MinorVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(12), (uint)Type);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(16), SizeOfData);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(20), AddressOfRawData);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(24), PointerToRawData);
        }
    }
}
