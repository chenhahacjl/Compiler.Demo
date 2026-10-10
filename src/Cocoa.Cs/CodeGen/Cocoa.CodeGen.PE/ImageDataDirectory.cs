using System;
using System.Buffers.Binary;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_DATA_DIRECTORY — 数据目录项（8 字节）。</summary>
    public readonly record struct ImageDataDirectory(uint VirtualAddress, uint Size)
    {
        public static int SizeOfEntry => 8;

        public static ImageDataDirectory Read(ReadOnlySpan<byte> s)
        {
            return new ImageDataDirectory(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, VirtualAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), Size);
        }
    }
}
