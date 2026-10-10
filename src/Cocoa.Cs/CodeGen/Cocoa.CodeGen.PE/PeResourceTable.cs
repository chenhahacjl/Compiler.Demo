using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    /// <summary>资源表读取器：展开三层目录树（类型 → 名称 → 语言）。</summary>
    public static class PeResourceTable
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
