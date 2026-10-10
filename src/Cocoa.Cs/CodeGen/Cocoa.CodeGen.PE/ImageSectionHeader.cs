using System;
using System.Buffers.Binary;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_SECTION_HEADER — 节表项（40 字节），Misc 按 VirtualSize 语义使用。</summary>
    public readonly record struct ImageSectionHeader(
        byte[] Name,
        uint VirtualSize,
        uint VirtualAddress,
        uint SizeOfRawData,
        uint PointerToRawData,
        uint PointerToRelocations,
        uint PointerToLinenumbers,
        ushort NumberOfRelocations,
        ushort NumberOfLinenumbers,
        uint Characteristics)
    {
        public static int Size => 40;

        public string NameString
        {
            get
            {
                var end = Array.IndexOf(Name, (byte)0);
                if (end < 0)
                {
                    end = Name.Length;
                }

                return System.Text.Encoding.ASCII.GetString(Name, 0, end);
            }
        }

        public static ImageSectionHeader Read(ReadOnlySpan<byte> s)
        {
            return new ImageSectionHeader(
                s.Slice(0, 8).ToArray(),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(12)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(16)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(20)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(24)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(28)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(32)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(34)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(36)));
        }

        public void Write(Span<byte> d)
        {
            Name.CopyTo(d.Slice(0, 8));
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(8), VirtualSize);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(12), VirtualAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(16), SizeOfRawData);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(20), PointerToRawData);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(24), PointerToRelocations);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(28), PointerToLinenumbers);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(32), NumberOfRelocations);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(34), NumberOfLinenumbers);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(36), Characteristics);
        }
    }
}
