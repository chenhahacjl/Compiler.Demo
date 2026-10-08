using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Cocoa.CodeGen.PE;

namespace Cocoa.CodeGen.Native.Metadata
{
    /// <summary>
    /// M3：Cocoa 产物元数据读取器。解析 `.cocoa` 根（"COCOA" 魔数 + 流头表 + 七流）为
    /// 对象模型（<see cref="CocoaMetadataModel"/>）。与 <see cref="CocoaMetadataBuilder"/> 互为逆。
    /// </summary>
    public sealed class CocoaMetadataReader
    {
        private readonly byte[] _root;
        private readonly Dictionary<string, List<MetadataRootStream.StreamHeader>> _streams = new(StringComparer.Ordinal);

        public CocoaMetadataReader(byte[] root)
        {
            _root = root;
            var magic = Encoding.ASCII.GetString(root, 0, 5);
            if (magic != "COCOA")
            {
                throw new InvalidDataException($"非 Cocoa 元数据根（魔数='{magic}'）");
            }

            var streamCount = root[6] | (root[7] << 8);
            var headers = MetadataRootStream.ReadHeaders(root, streamCount, 0x0A);
            foreach (var header in headers)
            {
                if (!_streams.TryGetValue(header.Name, out var list))
                {
                    _streams[header.Name] = list = new List<MetadataRootStream.StreamHeader>();
                }

                list.Add(header);
            }
        }

        public CocoaMetadataModel Read()
        {
            var strings = ReadStringHeap();
            var blobs = ReadBlobHeap();

            var types = ReadTypes(strings);
            var methods = ReadMethods(strings);
            var fields = ReadFields(strings);
            var attrs = ReadAttrs(strings, blobs);
            var docs = ReadDocs(strings);

            return new CocoaMetadataModel(types, methods, fields, attrs, docs, strings, blobs);
        }

        // ------------------------------------------------------------------
        // 堆
        // ------------------------------------------------------------------

        private Dictionary<int, string> ReadStringHeap()
        {
            var result = new Dictionary<int, string>();
            if (!_streams.TryGetValue("#Strings", out var list))
            {
                return result;
            }

            var data = MetadataRootStream.ReadStream(_root, list[0]);
            var offset = 0;
            while (offset < data.Length)
            {
                result[offset] = ReadNullTerminated(data, offset);
                while (offset < data.Length && data[offset] != 0)
                {
                    offset++;
                }

                offset++; // 跳过 \0
            }

            return result;
        }

        private Dictionary<int, byte[]> ReadBlobHeap()
        {
            var result = new Dictionary<int, byte[]>();
            if (!_streams.TryGetValue("#Blob", out var list))
            {
                return result;
            }

            var data = MetadataRootStream.ReadStream(_root, list[0]);
            // 偏移 0 预留给空 blob 哨兵（与 Builder 对齐）
            for (var offset = 1; offset < data.Length;)
            {
                if (offset + 4 > data.Length)
                {
                    break;
                }

                var length = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);
                offset += 4;
                if (offset + length > data.Length)
                {
                    break;
                }

                var blob = new byte[length];
                Array.Copy(data, offset, blob, 0, length);
                result[offset - 4] = blob; // key = 长度前缀起始（相对首先哨兵）
                offset += length;
            }

            return result;
        }

        // ------------------------------------------------------------------
        // 表
        // ------------------------------------------------------------------

        private List<CocoaMetadataModel.TypeRow> ReadTypes(Dictionary<int, string> strings)
        {
            var result = new List<CocoaMetadataModel.TypeRow>();
            if (!_streams.TryGetValue("#Types", out var list))
            {
                return result;
            }

            var data = MetadataRootStream.ReadStream(_root, list[0]);
            for (var i = 0; i + 36 <= data.Length; i += 36)
            {
                var row = new CocoaMetadataModel.TypeRow
                {
                    FullName = strings[ReadInt32(data, i)],
                    TypeKind = data[i + 4],
                    BaseFullName = ReadInt32(data, i + 8) == 0 ? null : strings[ReadInt32(data, i + 8)],
                    MethodStart = ReadInt32(data, i + 12),
                    MethodCount = ReadInt32(data, i + 16),
                    FieldStart = ReadInt32(data, i + 20),
                    FieldCount = ReadInt32(data, i + 24),
                    AttrRow = ReadInt32(data, i + 28),
                    AttrCount = ReadInt32(data, i + 32),
                };
                result.Add(row);
            }

            return result;
        }

        private List<CocoaMetadataModel.MethodRow> ReadMethods(Dictionary<int, string> strings)
        {
            var result = new List<CocoaMetadataModel.MethodRow>();
            if (!_streams.TryGetValue("#Methods", out var list))
            {
                return result;
            }

            var data = MetadataRootStream.ReadStream(_root, list[0]);
            for (var i = 0; i + 24 <= data.Length; i += 24)
            {
                var row = new CocoaMetadataModel.MethodRow
                {
                    FullName = strings[ReadInt32(data, i)],
                    OwnerType = ReadInt32(data, i + 4) == 0 ? null : strings[ReadInt32(data, i + 4)],
                    ReturnType = strings[ReadInt32(data, i + 8)],
                    Line = ReadInt32(data, i + 12),
                    IsStatic = data[i + 16] == 1,
                    AttrRow = ReadInt32(data, i + 20),
                };
                result.Add(row);
            }

            return result;
        }

        private List<CocoaMetadataModel.FieldRow> ReadFields(Dictionary<int, string> strings)
        {
            var result = new List<CocoaMetadataModel.FieldRow>();
            if (!_streams.TryGetValue("#Fields", out var list))
            {
                return result;
            }

            var data = MetadataRootStream.ReadStream(_root, list[0]);
            for (var i = 0; i + 20 <= data.Length; i += 20)
            {
                var row = new CocoaMetadataModel.FieldRow
                {
                    Name = strings[ReadInt32(data, i)],
                    Type = strings[ReadInt32(data, i + 4)],
                    OwnerType = strings[ReadInt32(data, i + 8)],
                    IsStatic = data[i + 12] == 1,
                    AttrRow = ReadInt32(data, i + 16),
                };
                result.Add(row);
            }

            return result;
        }

        private List<CocoaMetadataModel.AttrRow> ReadAttrs(Dictionary<int, string> strings, Dictionary<int, byte[]> blobs)
        {
            var result = new List<CocoaMetadataModel.AttrRow>();
            if (!_streams.TryGetValue("#Attrs", out var list))
            {
                return result;
            }

            var data = MetadataRootStream.ReadStream(_root, list[0]);
            for (var i = 0; i + 16 <= data.Length; i += 16)
            {
                var blobOffset = ReadInt32(data, i + 4);
                var row = new CocoaMetadataModel.AttrRow
                {
                    TypeFullName = strings[ReadInt32(data, i)],
                    BlobOffset = blobOffset,
                    ArgCount = ReadInt32(data, i + 8),
                    Blob = blobOffset == 0 ? null : blobs[blobOffset],
                };
                result.Add(row);
            }

            return result;
        }

        private List<CocoaMetadataModel.DocEntry> ReadDocs(Dictionary<int, string> strings)
        {
            var result = new List<CocoaMetadataModel.DocEntry>();
            if (!_streams.TryGetValue("#Docs", out var list))
            {
                return result;
            }

            var data = MetadataRootStream.ReadStream(_root, list[0]);
            for (var i = 0; i + 8 <= data.Length; i += 8)
            {
                var row = new CocoaMetadataModel.DocEntry
                {
                    DocId = strings[ReadInt32(data, i)],
                    Text = strings[ReadInt32(data, i + 4)],
                };
                result.Add(row);
            }

            return result;
        }

        // ------------------------------------------------------------------
        // 辅助
        // ------------------------------------------------------------------

        private static int ReadInt32(byte[] data, int offset)
            => data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);

        private static string ReadNullTerminated(byte[] data, int start)
        {
            var end = start;
            while (end < data.Length && data[end] != 0)
            {
                end++;
            }

            return Encoding.UTF8.GetString(data, start, end - start);
        }
    }

    /// <summary>M3 round-trip 对象模型（Reader 输出，与 Builder 输入可比）。</summary>
    public sealed class CocoaMetadataModel
    {
        public CocoaMetadataModel(
            List<TypeRow> types,
            List<MethodRow> methods,
            List<FieldRow> fields,
            List<AttrRow> attrs,
            List<DocEntry> docs,
            Dictionary<int, string> strings,
            Dictionary<int, byte[]> blobs)
        {
            Types = types;
            Methods = methods;
            Fields = fields;
            Attrs = attrs;
            Docs = docs;
            Strings = strings;
            Blobs = blobs;
        }

        public List<TypeRow> Types { get; }
        public List<MethodRow> Methods { get; }
        public List<FieldRow> Fields { get; }
        public List<AttrRow> Attrs { get; }
        public List<DocEntry> Docs { get; }
        public Dictionary<int, string> Strings { get; }
        public Dictionary<int, byte[]> Blobs { get; }

        public sealed class TypeRow
        {
            public string FullName = "";
            public byte TypeKind;
            public string? BaseFullName;
            public int MethodStart;
            public int MethodCount;
            public int FieldStart;
            public int FieldCount;
            public int AttrRow;
            public int AttrCount;
        }

        public sealed class MethodRow
        {
            public string FullName = "";
            public string? OwnerType;
            public string ReturnType = "";
            public int Line;
            public bool IsStatic;
            public int AttrRow;
        }

        public sealed class FieldRow
        {
            public string Name = "";
            public string Type = "";
            public string OwnerType = "";
            public bool IsStatic;
            public int AttrRow;
        }

        public sealed class AttrRow
        {
            public string TypeFullName = "";
            public int BlobOffset;
            public int ArgCount;
            public byte[]? Blob;
        }

        public sealed class DocEntry
        {
            public string DocId = "";
            public string Text = "";
        }
    }
}
