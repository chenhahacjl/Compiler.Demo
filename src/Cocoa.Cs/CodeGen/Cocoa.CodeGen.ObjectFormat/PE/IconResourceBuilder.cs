using System;
using System.Collections.Generic;
using System.IO;

namespace Cocoa.CodeGen.ObjectFormat.PE
{
    /// <summary>
    /// 应用图标（.ico）→ PE 资源（RT_GROUP_ICON + RT_ICON[]）转换。
    /// .ico 文件结构：ICONDIR（6B）+ ICONDIRENTRY[count]（16B each）+ 各图像原始数据。
    /// PE 需要：RT_GROUP_ICON（类型 14）= GRPICONDIR（同 ICONDIR，但每条目是 14B：含 nID 指向 RT_ICON）；
    /// RT_ICON（类型 3）= 单个图像数据（名称 ID = 1..count）。语言 0x0409。
    /// </summary>
    public static class IconResourceBuilder
    {
        public const uint RtIcon = 3;
        public const uint RtGroupIcon = 14;
        public static readonly ushort LanguageId = 0x0409;

        /// <summary>解析 .ico 字节 → PeResourceData 列表（顺序：group 在前，icon 1..N 在后）。null = 输入不是有效 ICO。</summary>
        public static IReadOnlyList<PeResourceData>? Build(byte[] icoData)
        {
            if (icoData == null || icoData.Length < 6)
            {
                return null;
            }

            var reserved = ReadUInt16(icoData, 0);
            var type = ReadUInt16(icoData, 2);
            var count = ReadUInt16(icoData, 4);
            if (reserved != 0 || type != 1 || count == 0)
            {
                return null;
            }

            // 每条目 16B；图像数据偏移在文件内
            var entries = new List<(byte Width, byte Height, byte ColorCount, ushort Planes, ushort BitCount, uint BytesInRes, uint ImageOffset)>();
            var group = new List<byte>();
            group.AddRange(new byte[] { 0, 0, 1, 0 }); // ICONDIR: reserved=0, type=1
            group.AddRange(new byte[] { (byte)count, 0 }); // count（u16 LE）

            for (var i = 0; i < count; i++)
            {
                var entryOffset = 6 + i * 16;
                if (entryOffset + 16 > icoData.Length)
                {
                    return null;
                }

                var width = icoData[entryOffset];
                var height = icoData[entryOffset + 1];
                var colorCount = icoData[entryOffset + 2];
                var planes = ReadUInt16(icoData, entryOffset + 4);
                var bitCount = ReadUInt16(icoData, entryOffset + 6);
                var bytesInRes = ReadUInt32(icoData, entryOffset + 8);
                var imageOffset = ReadUInt32(icoData, entryOffset + 12);

                entries.Add((width, height, colorCount, planes, bitCount, bytesInRes, imageOffset));

                // GRPICONDIRENTRY（14B）：宽/高/色数 + wPlanes/wBitCount + dwBytesInRes + nID
                group.Add(width);
                group.Add(height);
                group.Add(colorCount);
                group.Add(0);
                group.AddRange(new byte[] { (byte)planes, (byte)(planes >> 8) });
                group.AddRange(new byte[] { (byte)bitCount, (byte)(bitCount >> 8) });
                group.AddRange(new byte[] { (byte)bytesInRes, (byte)(bytesInRes >> 8), (byte)(bytesInRes >> 16), (byte)(bytesInRes >> 24) });
                var id = (ushort)(i + 1);
                group.AddRange(new byte[] { (byte)id, (byte)(id >> 8) });
            }

            var result = new List<PeResourceData>
            {
                new PeResourceData(RtGroupIcon, 1, LanguageId, group.ToArray()),
            };

            for (var i = 0; i < count; i++)
            {
                var (_, _, _, _, _, bytesInRes, imageOffset) = entries[i];
                if (imageOffset + bytesInRes > icoData.Length)
                {
                    return null;
                }

                var image = new byte[bytesInRes];
                Array.Copy(icoData, (int)imageOffset, image, 0, (int)bytesInRes);
                result.Add(new PeResourceData(RtIcon, (uint)(i + 1), LanguageId, image));
            }

            return result;
        }

        private static ushort ReadUInt16(byte[] b, int off) => (ushort)(b[off] | (b[off + 1] << 8));
        private static uint ReadUInt32(byte[] b, int off) => (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
    }
}