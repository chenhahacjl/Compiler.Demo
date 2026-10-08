using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    /// <summary>
    /// ECMA-335 风格「流集合」读写工具：魔数之后接任意命名流（流头表 + 各流数据），
    /// 供两种独立根复用：
    ///   ① IL 元数据根（BSJB 魔数，ManagedPEWriter 现用）；
    ///   ② .cocoa 产物元数据根（"COCOA" 魔数，native 产物自省）。
    /// 流头布局对齐 .NET：name（ASCII + '\0'，4 字节对齐）+ offset:u32 + size:u32，
    /// 其中 offset 相对根起点（魔数处）。
    /// </summary>
    public static class MetadataRootStream
    {
        /// <summary>单个流的名称/偏移/大小描述。</summary>
        public readonly struct StreamHeader
        {
            public readonly string Name;
            public readonly int Offset;
            public readonly int Size;

            public StreamHeader(string name, int offset, int size)
            {
                Name = name;
                Offset = offset;
                Size = size;
            }
        }

        /// <summary>
        /// 计算每个流的相对根起点偏移（4 字节对齐，首个流紧跟流头表之后）。
        /// </summary>
        /// <param name="headerBlockSize">流头表区大小（魔数头长度 + 各流头 4 字节对齐后的总长）。</param>
        public static int[] ComputeOffsets(int headerBlockSize, IReadOnlyList<(string Name, byte[] Data)> streams)
        {
            var offsets = new int[streams.Count];
            var offset = Align4(headerBlockSize);
            for (var i = 0; i < streams.Count; i++)
            {
                offsets[i] = offset;
                offset += Align4(streams[i].Data.Length);
            }

            return offsets;
        }

        /// <summary>写流头表（从 headerBlockBase 起），每条：name+\0+pad + offset + size。返回流头表结束（相对根起点）。</summary>
        public static int WriteHeaders(byte[] root, int rootBase, int headerBlockBase, IReadOnlyList<(string Name, byte[] Data)> streams, int[] offsets)
        {
            var pos = headerBlockBase;
            for (var i = 0; i < streams.Count; i++)
            {
                WriteInt32(root, rootBase + pos, offsets[i]);
                var size = Align4(streams[i].Data.Length);
                WriteInt32(root, rootBase + pos + 4, size);

                var nameBytes = Encoding.ASCII.GetBytes(streams[i].Name + "\0");
                nameBytes.CopyTo(root, rootBase + pos + 8);
                pos += 8 + Align4(nameBytes.Length);
            }

            return pos;
        }

        /// <summary>写各流数据（4 字节对齐填充），紧随流头表。</summary>
        public static void WriteData(byte[] root, int rootBase, IReadOnlyList<(string Name, byte[] Data)> streams, int[] offsets)
        {
            for (var i = 0; i < streams.Count; i++)
            {
                var data = streams[i].Data;
                var offset = rootBase + offsets[i];
                data.CopyTo(root, offset);
                // 数据区 4 字节对齐：头部已算入 Align4，此处无需额外补零（root 已预清零）
            }
        }

        /// <summary>读取流头表：返回名称 → (偏移, 大小)。布局与写侧一致：offset:u32 + size:u32 + name\0+pad。</summary>
        public static List<StreamHeader> ReadHeaders(ReadOnlySpan<byte> root, int streamCount, int headerBlockBase)
        {
            var result = new List<StreamHeader>(streamCount);
            var nameCache = new Dictionary<string, int>(StringComparer.Ordinal);
            var pos = headerBlockBase;
            for (var i = 0; i < streamCount; i++)
            {
                var offset = BinaryPrimitives.ReadInt32LittleEndian(root.Slice(pos));
                var size = BinaryPrimitives.ReadInt32LittleEndian(root.Slice(pos + 4));
                var nameBytes = ReadFixedName(root, pos + 8);

                // 去重：同名流只保留首个（对齐现有 MetadataRoot 行为：同实例去重）
                if (!nameCache.TryGetValue(nameBytes, out var existingIdx))
                {
                    nameCache[nameBytes] = result.Count;
                    result.Add(new StreamHeader(nameBytes, offset, size));
                }

                pos += 8 + Align4(Encoding.ASCII.GetByteCount(nameBytes) + 1);
            }

            return result;
        }

        /// <summary>读取指定流数据（按偏移/大小从根切片）。</summary>
        public static byte[] ReadStream(ReadOnlySpan<byte> root, StreamHeader header)
            => root.Slice(header.Offset, header.Size).ToArray();

        private static string ReadFixedName(ReadOnlySpan<byte> root, int pos)
        {
            var end = pos;
            while (end < root.Length && root[end] != 0)
            {
                end++;
            }

            return Encoding.ASCII.GetString(root.Slice(pos, end - pos));
        }

        private static int Align4(int value) => (value + 3) & ~3;

        private static void WriteInt32(byte[] bytes, int index, int value)
        {
            bytes[index] = (byte)value;
            bytes[index + 1] = (byte)(value >> 8);
            bytes[index + 2] = (byte)(value >> 16);
            bytes[index + 3] = (byte)(value >> 24);
        }
    }
}