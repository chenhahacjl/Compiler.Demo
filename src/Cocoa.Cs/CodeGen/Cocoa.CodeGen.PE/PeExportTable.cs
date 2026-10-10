using System;
using System.Buffers.Binary;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    /// <summary>导出表解析：名字数组 + ordinal 表定位导出，forwarder 判定。stub 机器码的参照实现。</summary>
    public static class PeExportTable
    {
        /// <summary>
        /// 在指定导出目录中按名字查找函数。大小写敏感（PE 规范语义）。
        /// 命中返回函数 RVA（非 forwarder）或 forwarder 字符串入口（rdx 模式经 forwarder 目标解析）。
        /// </summary>
        public static bool TryGetExportRva(ReadOnlySpan<byte> image, Func<uint, uint> rvaToOffset, PeExportIndex index, string functionName, out uint rva)
        {
            rva = 0;
            if (index.ExportDirRva == 0 || index.NameCount == 0 || index.NamesRva == 0)
            {
                return false;
            }

            var namesRva = index.NamesRva;
            for (uint i = 0; i < index.NameCount; i++)
            {
                var namePtrOffset = rvaToOffset(namesRva + (uint)i * 4);
                if (namePtrOffset + 4 > (uint)image.Length)
                {
                    return false;
                }

                var nameRva = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice((int)namePtrOffset, 4));
                if (nameRva == 0)
                {
                    continue;
                }

                var nameOffset = rvaToOffset(nameRva);
                if (nameOffset >= (uint)image.Length)
                {
                    continue;
                }

                var end = (int)nameOffset;
                while (end < image.Length && image[end] != 0)
                {
                    end++;
                }

                var length = end - (int)nameOffset;
                if (length == functionName.Length && ImageEquals(image.Slice((int)nameOffset, length), functionName))
                {
                    var ordinalOffset = rvaToOffset(index.NameOrdinalsRva + (uint)i * 2);
                    if (ordinalOffset + 2 > (uint)image.Length)
                    {
                        return false;
                    }

                    var ordinalIndex = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice((int)ordinalOffset, 2));
                    var functionOffset = rvaToOffset(index.FunctionsRva + (uint)ordinalIndex * 4);
                    if (functionOffset + 4 > (uint)image.Length)
                    {
                        return false;
                    }

                    rva = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice((int)functionOffset, 4));
                    return true;
                }
            }

            return false;
        }

        /// <summary>判断 RVA 是否落在导出目录区间内（即 forwarder 字符串表）。</summary>
        public static bool IsForwarder(PeExportIndex index, uint rva)
        {
            return rva >= index.ExportDirRva && rva < index.ExportDirRva + ImageExportDirectory.SizeOfEntry + index.FunctionCount * 4 + index.NameCount * 4 + index.NameCount * 2;
        }

        /// <summary>读取 forwarder 字符串（如 "KERNEL32.ExportName"），返回 (DllName, FunctionName)。</summary>
        public static (string DllName, string FunctionName) ReadForwarder(ReadOnlySpan<byte> image, Func<uint, uint> rvaToOffset, uint forwarderRva)
        {
            var offset = rvaToOffset(forwarderRva);
            var end = (int)offset;
            while (end < image.Length && image[end] != 0)
            {
                end++;
            }

            var text = Encoding.ASCII.GetString(image.Slice((int)offset, end - (int)offset));
            var dot = text.IndexOf('.');
            return dot < 0 ? (text, string.Empty) : (text.Substring(0, dot), text.Substring(dot + 1));
        }

        private static bool ImageEquals(ReadOnlySpan<byte> bytes, string value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                if (bytes[i] != (byte)value[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
