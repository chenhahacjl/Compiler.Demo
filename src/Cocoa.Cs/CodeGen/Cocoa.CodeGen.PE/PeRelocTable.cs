using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Cocoa.CodeGen.PE
{
    /// <summary>重定位块序列解析。</summary>
    public static class PeRelocTable
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
