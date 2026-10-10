using System;
using System.Buffers.Binary;

namespace Cocoa.CodeGen.PE
{
    public readonly record struct ImageLoadConfigCodeIntegrity(
        ushort Flags,
        ushort Catalog,
        uint CatalogOffset)
    {
        public static int SizeOfEntry => 8;

        public static ImageLoadConfigCodeIntegrity Read(ReadOnlySpan<byte> s)
        {
            return new ImageLoadConfigCodeIntegrity(
                BinaryPrimitives.ReadUInt16LittleEndian(s),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(2)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d, Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(2), Catalog);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), CatalogOffset);
        }
    }
}
