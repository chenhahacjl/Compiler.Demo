using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeAnalysis.Emit.Native.PEFile
{
    /// <summary>IMAGE_RESOURCE_DIRECTORY — 资源目录（16 字节）。</summary>
    internal readonly record struct ImageResourceDirectory(
        uint Characteristics,
        uint TimeDateStamp,
        ushort MajorVersion,
        ushort MinorVersion,
        ushort NumberOfNamedEntries,
        ushort NumberOfIdEntries)
    {
        public static int SizeOfEntry => 16;

        public int EntryCount => NumberOfNamedEntries + NumberOfIdEntries;

        public static ImageResourceDirectory Read(ReadOnlySpan<byte> s)
        {
            return new ImageResourceDirectory(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(8)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(10)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(12)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(14)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, Characteristics);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), TimeDateStamp);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(8), MajorVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(10), MinorVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(12), NumberOfNamedEntries);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(14), NumberOfIdEntries);
        }
    }

    /// <summary>IMAGE_RESOURCE_DIRECTORY_ENTRY — 目录项（8 字节），高位为 NameIsString / DataIsDirectory。</summary>
    internal readonly record struct ImageResourceDirectoryEntry(uint Name, uint OffsetToData)
    {
        public const uint NameIsString = 0x80000000;
        public const uint DataIsDirectory = 0x80000000;

        public static int SizeOfEntry => 8;

        public bool NameIsStringFlag => (Name & NameIsString) != 0;

        public ushort NameId => unchecked((ushort)Name);

        public uint NameStringOffset => Name & ~NameIsString;

        public bool DataIsDirectoryFlag => (OffsetToData & DataIsDirectory) != 0;

        public uint OffsetToDataValue => OffsetToData & ~DataIsDirectory;

        public static ImageResourceDirectoryEntry Read(ReadOnlySpan<byte> s)
        {
            return new ImageResourceDirectoryEntry(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, Name);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), OffsetToData);
        }
    }

    /// <summary>IMAGE_RESOURCE_DIRECTORY_STRING — 资源目录名字符串（Length + UTF-16LE）。</summary>
    internal readonly record struct ImageResourceDirectoryString(ushort Length, byte[] Value)
    {
        public string ValueString => Encoding.Unicode.GetString(Value);

        public static ImageResourceDirectoryString Read(ReadOnlySpan<byte> s)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(s);
            return new ImageResourceDirectoryString(length, s.Slice(2, length * 2).ToArray());
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d, Length);
            Value.CopyTo(d.Slice(2));
        }
    }

    /// <summary>IMAGE_RESOURCE_DATA_ENTRY — 资源数据项（16 字节）。</summary>
    internal readonly record struct ImageResourceDataEntry(
        uint OffsetToData,
        uint Size,
        uint CodePage,
        uint Reserved)
    {
        public static int SizeOfEntry => 16;

        public static ImageResourceDataEntry Read(ReadOnlySpan<byte> s)
        {
            return new ImageResourceDataEntry(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(12)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, OffsetToData);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), Size);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(8), CodePage);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(12), Reserved);
        }
    }

    /// <summary>资源树节点（解析产物）。</summary>
    internal sealed record PeResourceNode(string Name, uint Id, IReadOnlyList<PeResourceNode> Children, PeResourceLeaf? Leaf);

    internal sealed record PeResourceLeaf(uint Rva, uint Size);

    /// <summary>资源表读取器：展开三层目录树（类型 → 名称 → 语言）。</summary>
    internal static class PeResourceTable
    {
        public static PeResourceNode? Read(ReadOnlySpan<byte> image, Func<uint, uint> rvaToOffset, uint rootRva)
        {
            if (rootRva == 0)
            {
                return null;
            }

            var rootOffset = rvaToOffset(rootRva);
            return ReadDirectory(image, rvaToOffset, rootRva, rootOffset, 0);
        }

        private static PeResourceNode ReadDirectory(ReadOnlySpan<byte> image, Func<uint, uint> rvaToOffset, uint directoryRva, uint directoryOffset, int depth)
        {
            var children = new List<PeResourceNode>();

            if (directoryOffset + ImageResourceDirectory.SizeOfEntry > (uint)image.Length)
            {
                return new PeResourceNode(string.Empty, 0, children, null);
            }

            var directory = ImageResourceDirectory.Read(image.Slice((int)directoryOffset, ImageResourceDirectory.SizeOfEntry));
            var entryBaseOffset = directoryOffset + ImageResourceDirectory.SizeOfEntry;

            for (var i = 0; i < directory.EntryCount; i++)
            {
                var entryOffset = entryBaseOffset + (uint)i * ImageResourceDirectoryEntry.SizeOfEntry;
                if (entryOffset + ImageResourceDirectoryEntry.SizeOfEntry > (uint)image.Length)
                {
                    break;
                }

                var entry = ImageResourceDirectoryEntry.Read(image.Slice((int)entryOffset, ImageResourceDirectoryEntry.SizeOfEntry));
                var name = string.Empty;
                uint id = 0;

                if (entry.NameIsStringFlag)
                {
                    var stringOffset = rvaToOffset(directoryRva + entry.NameStringOffset);
                    var resourceString = ImageResourceDirectoryString.Read(image.Slice((int)stringOffset));
                    name = resourceString.ValueString;
                }
                else
                {
                    id = entry.NameId;
                }

                PeResourceLeaf? leaf = null;
                IReadOnlyList<PeResourceNode> childNodes = Array.Empty<PeResourceNode>();

                if (entry.DataIsDirectoryFlag)
                {
                    var subDirectoryRva = directoryRva + entry.OffsetToDataValue;
                    childNodes = ReadDirectory(image, rvaToOffset, subDirectoryRva, rvaToOffset(subDirectoryRva), depth + 1).Children;
                }
                else if (depth == 2)
                {
                    var dataOffset = rvaToOffset(directoryRva + entry.OffsetToDataValue);
                    if (dataOffset + ImageResourceDataEntry.SizeOfEntry <= (uint)image.Length)
                    {
                        var data = ImageResourceDataEntry.Read(image.Slice((int)dataOffset, ImageResourceDataEntry.SizeOfEntry));
                        leaf = new PeResourceLeaf(data.OffsetToData, data.Size);
                    }
                }

                children.Add(new PeResourceNode(name, id, childNodes, leaf));
            }

            return new PeResourceNode(string.Empty, 0, children, null);
        }
    }
}