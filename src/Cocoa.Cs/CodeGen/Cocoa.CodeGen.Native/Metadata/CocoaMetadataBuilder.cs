using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeGen.Native.Metadata
{
    /// <summary>
    /// M3：Cocoa 产物元数据构建器。把「发射后符号快照」（<see cref="EmittedSymbolSnapshot"/>）序列化为
    /// `.cocoa` 根的七个流：#Types / #Methods / #Fields / #Attrs + #Strings / #Blob + #Docs。
    /// 表行定宽（行号交叉引用）、字符串/实参走堆偏移、属性实参对齐 CLR CustomAttribute Value blob。
    /// </summary>
    public sealed class CocoaMetadataBuilder
    {
        private readonly ImmutableArray<NamedTypeSymbol> _liveClasses;
        private readonly ImmutableDictionary<FunctionSymbol, Lir.LirFunction> _functions;

        private readonly Dictionary<string, int> _stringOffsets = new(StringComparer.Ordinal);
        private readonly List<byte> _strings = new();

        private readonly Dictionary<ImmutableArray<byte>, int> _blobOffsets = new(BlobComparer.Instance);
        private readonly List<byte> _blobs = new();

        private readonly List<byte> _types = new();
        private readonly List<byte> _methods = new();
        private readonly List<byte> _fields = new();
        private readonly List<byte> _attrs = new();
        private readonly List<byte> _docs = new();

        public CocoaMetadataBuilder(EmittedSymbolSnapshot snapshot)
        {
            _liveClasses = snapshot.LiveClasses;
            _functions = snapshot.Functions;
        }

        private sealed class BlobComparer : IEqualityComparer<ImmutableArray<byte>>
        {
            public static readonly BlobComparer Instance = new();

            public bool Equals(ImmutableArray<byte> x, ImmutableArray<byte> y)
                => x.AsSpan().SequenceEqual(y.AsSpan());

            public int GetHashCode(ImmutableArray<byte> obj)
            {
                var hash = new HashCode();
                foreach (var b in obj)
                {
                    hash.Add(b);
                }

                return hash.ToHashCode();
            }
        }

        /// <summary>把快照序列化为 .cocoa 根字节（"COCOA" 魔数 + 流头表 + 七流数据）。</summary>
        public byte[] Build()
        {
            EmitTypesAndMembers();
            EmitDocs();

            var streams = new List<(string, byte[])>
            {
                ("#Types", _types.ToArray()),
                ("#Methods", _methods.ToArray()),
                ("#Fields", _fields.ToArray()),
                ("#Attrs", _attrs.ToArray()),
                ("#Strings", _strings.ToArray()),
                ("#Blob", _blobs.ToArray()),
                ("#Docs", _docs.ToArray()),
            };

            // .cocoa 根：魔数(0x0A) + 流头表 + 各流
            const int magicHeaderSize = 0x0A;
            var headerBlockSize = magicHeaderSize;
            foreach (var (name, _) in streams)
            {
                headerBlockSize += 8 + Align4(Encoding.ASCII.GetByteCount(name) + 1);
            }

            var offsets = PE.MetadataRootStream.ComputeOffsets(headerBlockSize, streams);
            var lastDataEnd = headerBlockSize;
            foreach (var (name, data) in streams)
            {
                var end = offsets[streams.IndexOf((name, data))] + Align4(data.Length);
                if (end > lastDataEnd)
                {
                    lastDataEnd = end;
                }
            }

            var root = new byte[lastDataEnd];
            Encoding.ASCII.GetBytes("COCOA").CopyTo(root, 0);
            root[5] = 1; // 版本
            // 标志(2B) 预留 0
            root[6] = (byte)streams.Count;
            root[7] = (byte)(streams.Count >> 8);

            PE.MetadataRootStream.WriteHeaders(root, 0, magicHeaderSize, streams, offsets);
            PE.MetadataRootStream.WriteData(root, 0, streams, offsets);
            return root;
        }

        // ------------------------------------------------------------------
        // 表行布局（定宽行 → 行号交叉引用）
        //   #Types   行 36B：FullName:u32 | TypeKind:u8 | pad:3 | BaseFullName:u32
        //             | MethodStart:u32 | MethodCount:u32 | FieldStart:u32 | FieldCount:u32
        //             | AttrRow:u32 | AttrCount:u32
        //   #Methods 行 24B：FullName:u32 | OwnerType:u32 | ReturnType:u32 | Line:u32
        //             | Static:u8 | pad:3 | AttrRow:u32
        //   #Fields  行 20B：Name:u32 | Type:u32 | OwnerType:u32 | Static:u8 | pad:3
        //             | AttrRow:u32
        //   #Attrs   行 16B：TypeFullName:u32 | BlobOffset:u32 | ArgCount:u32 | pad:4
        // ------------------------------------------------------------------

        private const int TypeRowSize = 36;
        private const int MethodRowSize = 24;
        private const int FieldRowSize = 20;
        private const int AttrRowSize = 16;

        private void EmitTypesAndMembers()
        {
            var classesInOrder = _liveClasses.OrderBy(c => c.FullName, StringComparer.Ordinal).ToArray();

            // 第一遍：为所有字符串/实参分配堆偏移（interning 与顺序无关）
            InternAllStrings(classesInOrder);

            // 第二遍：单遍生成 类型行 + 方法区段 + 字段区段（行号交叉引用同步推进）
            var methodStart = 0;
            var fieldStart = 0;
            foreach (var cls in classesInOrder)
            {
                var methods = cls.Methods
                    .Where(m => _functions.ContainsKey(m))
                    .OrderBy(m => m.Name, StringComparer.Ordinal)
                    .ToArray();

                var fields = cls.Fields.OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();

                // 类型行
                var baseOffset = cls.BaseType == null ? 0 : InternString(cls.BaseType.FullName);
                WriteInt32(_types, _types.Count, InternString(cls.FullName));
                _types.Add((byte)cls.TypeKind);
                _types.AddRange(new byte[3]); // pad
                WriteInt32(_types, _types.Count, baseOffset);
                WriteInt32(_types, _types.Count, methodStart);
                WriteInt32(_types, _types.Count, methods.Length);
                WriteInt32(_types, _types.Count, fieldStart);
                WriteInt32(_types, _types.Count, fields.Length);
                var attrRow = EmitAttrs(cls.Attributes);
                WriteInt32(_types, _types.Count, attrRow);
                WriteInt32(_types, _types.Count, cls.Attributes.Length);

                foreach (var m in methods)
                {
                    WriteMethodRow(m, InternString(cls.FullName));
                }

                foreach (var f in fields)
                {
                    WriteFieldRow(f);
                }

                methodStart += methods.Length;
                fieldStart += fields.Length;
            }

            // 顶层函数追加到方法表末尾（属主类偏移 0，类型行不引用）
            foreach (var f in _functions.Keys
                .Where(f => f.ContainingClass == null)
                .OrderBy(FunctionDisplayName, StringComparer.Ordinal))
            {
                WriteMethodRow(f, 0);
            }
        }

        /// <summary>全部字符串/属性类型名先入堆（保证后续所有 InternString 命中缓存）。</summary>
        private void InternAllStrings(NamedTypeSymbol[] classesInOrder)
        {
            foreach (var cls in classesInOrder)
            {
                _ = InternString(cls.FullName);
                if (cls.BaseType != null)
                {
                    _ = InternString(cls.BaseType.FullName);
                }

                foreach (var attr in cls.Attributes)
                {
                    _ = InternString(attr.Type.FullName);
                }
            }

            foreach (var (func, _) in _functions)
            {
                _ = InternString(FunctionDisplayName(func));
                if (func.ContainingClass != null)
                {
                    _ = InternString(func.ContainingClass.FullName);
                }

                _ = InternString(TypeDisplayName(func.ReturnType));
                foreach (var attr in func.Attributes)
                {
                    _ = InternString(attr.Type.FullName);
                }
            }

            foreach (var cls in classesInOrder)
            {
                foreach (var field in cls.Fields)
                {
                    _ = InternString(field.Name);
                    _ = InternString(TypeDisplayName(field.Type));
                    _ = InternString(cls.FullName);
                    foreach (var attr in field.Attributes)
                    {
                        _ = InternString(attr.Type.FullName);
                    }
                }
            }
        }

        private void WriteMethodRow(FunctionSymbol symbol, int ownerOffset)
        {
            var fullName = InternString(FunctionDisplayName(symbol));
            var returnOffset = InternString(TypeDisplayName(symbol.ReturnType));
            var line = GetLine(symbol);
            var attrRow = EmitAttrs(symbol.Attributes);

            WriteInt32(_methods, _methods.Count, fullName);
            WriteInt32(_methods, _methods.Count, ownerOffset);
            WriteInt32(_methods, _methods.Count, returnOffset);
            WriteInt32(_methods, _methods.Count, line);
            _methods.Add((byte)(symbol.IsStatic ? 1 : 0));
            _methods.AddRange(new byte[3]); // pad
            WriteInt32(_methods, _methods.Count, attrRow); // 行 24B 定宽
        }

        private void WriteFieldRow(FieldSymbol symbol)
        {
            WriteInt32(_fields, _fields.Count, InternString(symbol.Name));
            WriteInt32(_fields, _fields.Count, InternString(TypeDisplayName(symbol.Type)));
            WriteInt32(_fields, _fields.Count, InternString(symbol.ContainingClass.FullName));
            _fields.Add((byte)(symbol.IsStatic ? 1 : 0));
            _fields.AddRange(new byte[3]); // pad
            WriteInt32(_fields, _fields.Count, EmitAttrs(symbol.Attributes)); // 行 20B 定宽
        }

        /// <summary>写一组属性行（定宽 16B），返回首行号（无属性返回 0）。</summary>
        private int EmitAttrs(ImmutableArray<AttributeSymbol> attrs)
        {
            if (attrs.Length == 0)
            {
                return 0;
            }

            var firstRow = _attrs.Count / AttrRowSize;
            foreach (var attr in attrs)
            {
                var typeName = InternString(attr.Type.FullName);
                var blobOffset = EmitArgumentBlob(attr.Arguments);

                WriteInt32(_attrs, _attrs.Count, typeName);
                WriteInt32(_attrs, _attrs.Count, blobOffset);
                WriteInt32(_attrs, _attrs.Count, attr.Arguments.Length);
                _attrs.AddRange(new byte[4]); // pad
            }

            return firstRow;
        }

        /// <summary>CLR 风格属性实参 blob：长度前缀 + 逐实参（类型名 + 值）。</summary>
        private int EmitArgumentBlob(ImmutableArray<(TypeSymbol Type, object Value)> args)
        {
            if (args.Length == 0)
            {
                return 0;
            }

            var blob = new List<byte>();
            foreach (var (type, value) in args)
            {
                var typeName = Encoding.UTF8.GetBytes(TypeDisplayName(type));
                WriteUInt32(blob, blob.Count, (uint)typeName.Length);
                blob.AddRange(typeName);
                WriteArgumentValue(blob, type, value);
            }

            var bytes = blob.ToArray();
            if (_blobOffsets.TryGetValue(ImmutableArray.Create(bytes), out var existing))
            {
                return existing;
            }

            // 偏移 0 预留给空 blob 哨兵（表字段用 0 表示「无 blob」，避免与真实 blob 冲突）
            if (_blobs.Count == 0)
            {
                _blobs.Add(0);
            }

            var offset = _blobs.Count;
            _blobOffsets[ImmutableArray.Create(bytes)] = offset;
            // 长度前缀（u32）+ 内容
            WriteUInt32(_blobs, _blobs.Count, (uint)bytes.Length);
            _blobs.AddRange(bytes);
            return offset;
        }

        private void WriteArgumentValue(List<byte> blob, TypeSymbol type, object value)
        {
            switch (value)
            {
                case string s:
                    var sb = Encoding.UTF8.GetBytes(s);
                    WriteUInt32(blob, blob.Count, (uint)sb.Length);
                    blob.AddRange(sb);
                    break;
                case bool b:
                    blob.Add((byte)(b ? 1 : 0));
                    break;
                case int i:
                    WriteInt32(blob, blob.Count, i);
                    break;
                case double d:
                    blob.AddRange(BitConverter.GetBytes(d));
                    break;
                case null:
                    blob.Add(0);
                    break;
                default:
                    throw new NotSupportedException($"M3 属性实参值类型不支持：{value.GetType()}");
            }
        }

        // ------------------------------------------------------------------
        // Docs：DocID → 原文（"签名文档串"——Cocoa 无 doc 注释，落点为符号可读描述）
        // ------------------------------------------------------------------

        private void EmitDocs()
        {
            foreach (var cls in _liveClasses.OrderBy(c => c.FullName, StringComparer.Ordinal))
            {
                var docId = InternString(cls.FullName + "!Doc");
                var text = InternString(cls.FullName);
                WriteDocEntry(docId, text);

                foreach (var m in cls.Methods.Where(m => _functions.ContainsKey(m)))
                {
                    WriteDocEntry(InternString(FunctionDisplayName(m) + "!Doc"), InternString(FunctionDisplayName(m)));
                }
            }
        }

        private void WriteDocEntry(int docIdOffset, int textOffset)
        {
            WriteInt32(_docs, _docs.Count, docIdOffset);
            WriteInt32(_docs, _docs.Count, textOffset);
        }

        // ------------------------------------------------------------------
        // 堆
        // ------------------------------------------------------------------

        private int InternString(string value)
        {
            // 偏移 0 预留给空串哨兵（表字段用 0 表示「无引用」，避免与真实字符串冲突）
            if (_stringOffsets.Count == 0)
            {
                _strings.Add(0);
                _stringOffsets[string.Empty] = 0;
            }

            if (_stringOffsets.TryGetValue(value, out var offset))
            {
                return offset;
            }

            offset = _strings.Count;
            _stringOffsets[value] = offset;
            _strings.AddRange(Encoding.UTF8.GetBytes(value));
            _strings.Add(0);
            return offset;
        }

        // ------------------------------------------------------------------
        // 辅助
        // ------------------------------------------------------------------

        private static int GetLine(FunctionSymbol symbol)
            => symbol.Declaration?.Location.StartLine + 1 ?? 0;

        private static int GetLine(FieldSymbol symbol)
            => symbol.Name.Length; // 占位（字段无 Declaration 行号来源，见快照契约）

        private static string FunctionDisplayName(FunctionSymbol symbol)
            => symbol.ContainingClass == null
                ? (symbol.Namespace.Length == 0 ? "" : symbol.Namespace + ".") + symbol.Name
                : symbol.ContainingClass!.FullName + "." + symbol.Name;

        private static string TypeDisplayName(TypeSymbol type)
            => type is NamedTypeSymbol named ? named.FullName : type.Name;

        private static int Align4(int value) => (value + 3) & ~3;

        private static void WriteInt32(List<byte> bytes, int index, int value)
        {
            bytes.Add((byte)value);
            bytes.Add((byte)(value >> 8));
            bytes.Add((byte)(value >> 16));
            bytes.Add((byte)(value >> 24));
        }

        private static void WriteUInt32(List<byte> bytes, int index, uint value)
        {
            bytes.Add((byte)value);
            bytes.Add((byte)(value >> 8));
            bytes.Add((byte)(value >> 16));
            bytes.Add((byte)(value >> 24));
        }

        private static void WriteUInt16(List<byte> bytes, int index, ushort value)
        {
            bytes.Add((byte)value);
            bytes.Add((byte)(value >> 8));
        }
    }
}
