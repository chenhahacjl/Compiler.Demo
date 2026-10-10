using System;
using System.Collections.Generic;

namespace Cocoa.CodeGen.Native.Metadata
{
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