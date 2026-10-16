using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.ObjectFormat.PE
{
    /// <summary>资源表读取器：展开三层目录树（类型 → 名称 → 语言）。
    /// 目录项偏移遵循 PE 规范——相对 <c>IMAGE_DIRECTORY_ENTRY_RESOURCE</c>（资源节根 RVA）。</summary>
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

        private static PeResourceNode ReadDirectory(ReadOnlySpan<byte> image, Func<uint, uint> rvaToOffset, uint rootRva, uint directoryOffset, int depth)
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
                    // 名称字符串偏移同样相对资源节根
                    var stringOffset = rvaToOffset(rootRva + entry.NameStringOffset);
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
                    var subDirectoryRva = rootRva + entry.OffsetToDataValue;
                    childNodes = ReadDirectory(image, rvaToOffset, rootRva, rvaToOffset(subDirectoryRva), depth + 1).Children;
                }
                else if (depth >= 2)
                {
                    // 叶子：entry.OffsetToData 指向 IMAGE_RESOURCE_DATA_ENTRY（同样相对资源节根）
                    var dataEntryOffset = rvaToOffset(rootRva + entry.OffsetToDataValue);
                    if (dataEntryOffset + ImageResourceDataEntry.SizeOfEntry <= (uint)image.Length)
                    {
                        var data = ImageResourceDataEntry.Read(image.Slice((int)dataEntryOffset, ImageResourceDataEntry.SizeOfEntry));
                        leaf = new PeResourceLeaf(data.OffsetToData, data.Size);
                    }
                }

                children.Add(new PeResourceNode(name, id, childNodes, leaf));
            }

            return new PeResourceNode(string.Empty, 0, children, null);
        }
    }
}
