using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_BASE_RELOCATION — 基址重定位块（8 字节头 + TypeOffset 数组）。</summary>
    internal readonly record struct ImageBaseRelocation(uint VirtualAddress, uint SizeOfBlock)
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

    /// <summary>TypeOffset 解码：类型与偏移以 WORD 位域存储。</summary>
    internal readonly record struct PeRelocationEntry(PeRelocType Type, int Offset)
    {
        public static PeRelocationEntry FromWord(ushort value)
        {
            return new PeRelocationEntry((PeRelocType)(value >> 12), value & 0x0FFF);
        }

        public ushort ToWord()
        {
            return (ushort)(((int)Type << 12) | (Offset & 0x0FFF));
        }
    }

    internal sealed record PeRelocationBlock(uint PageRva, IReadOnlyList<PeRelocationEntry> Entries);

    /// <summary>重定位块序列解析。</summary>
    internal static class PeRelocTable
    {
        public static IReadOnlyList<PeRelocationBlock> Read(ReadOnlySpan<byte> image, Func<uint, uint> rvaToOffset, uint relocRva, uint relocSize)
        {
            var blocks = new List<PeRelocationBlock>();
            if (relocRva == 0 || relocSize == 0)
            {
                return blocks;
            }

            var offset = rvaToOffset(relocRva);
            var endOffset = offset + relocSize;

            while (offset + ImageBaseRelocation.SizeOfEntry <= endOffset)
            {
                var header = ImageBaseRelocation.Read(image.Slice((int)offset, ImageBaseRelocation.SizeOfEntry));
                if (header.SizeOfBlock == 0)
                {
                    break;
                }

                var entries = new List<PeRelocationEntry>(header.RelocationCount);
                for (var i = 0; i < header.RelocationCount; i++)
                {
                    var word = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice((int)offset + ImageBaseRelocation.SizeOfEntry + i * 2, 2));
                    entries.Add(PeRelocationEntry.FromWord(word));
                }

                blocks.Add(new PeRelocationBlock(header.VirtualAddress, entries));
                offset += header.SizeOfBlock;
            }

            return blocks;
        }
    }
}