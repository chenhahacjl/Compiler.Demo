using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Cocoa.CodeGen.PE
{
    /// <summary>
    /// 通用 .rsrc 资源节构建器：把多条 <see cref="PeResourceData"/>（RT_VERSION / RT_ICON / RT_GROUP_ICON /
    /// RT_RCDATA 等）组装为三层资源目录树 + 叶子数据。替代只支持单一 RT_VERSION 的 VersionResourceBuilder。
    ///
    /// 布局（偏移相对 .rsrc 节起点；与 PeResourceTable.Read 读侧对称）：
    ///   root dir →（按 Type）type dir →（按 NameId）name dir →（按 LanguageId）lang dir → DataEntry(16B) → 数据。
    /// 每个 dir 内 entry 数 = 子节点数（本实现全部为 ID 命名，NamedEntries=0）。
    /// 叶子 DataEntry.OffsetToData 先写节内相对偏移，调用方（PeFileWriter）按 .rsrc 节 RVA 回填。
    /// </summary>
    public static class ResourceSectionBuilder
    {
        public const uint RtVersion = 16;
        public const uint RtIcon = 3;
        public const uint RtGroupIcon = 14;
        public const uint RtRcData = 10;
        public const uint RtManifest = 24;

        public static readonly ushort DefaultLanguageId = 0x0409;

        /// <summary>构建 .rsrc 节完整数据。</summary>
        /// <param name="resources">资源条目（任意顺序；内部按 Type/NameId/LanguageId 稳定排序）。</param>
        /// <returns>blob + DataEntry.OffsetToData 字段偏移列表（每项对应一条资源，供 RVA 回填）。</returns>
        public static ResourceSectionResult Build(IReadOnlyList<PeResourceData> resources)
        {
            var ordered = resources
                .OrderBy(r => r.Type)
                .ThenBy(r => r.NameId)
                .ThenBy(r => r.LanguageId)
                .ToList();

            var blob = new MemoryStream();
            var dataEntryOffsets = new List<int>();

            // ---- root dir（16B）+ root entries（8B/个）----
            var typeKeys = ordered.Select(r => r.Type).Distinct().ToList();
            var rootEntryOffsets = WriteDirectoryWithEntries(blob, typeKeys);

            foreach (var typeKey in typeKeys)
            {
                var typeEntryOffset = (int)blob.Position;
                var typeItems = ordered.Where(r => r.Type == typeKey).ToList();
                var nameKeys = typeItems.Select(r => r.NameId).Distinct().ToList();
                var nameEntryOffsets = WriteDirectoryWithEntries(blob, nameKeys);

                foreach (var nameKey in nameKeys)
                {
                    var nameItems = typeItems.Where(r => r.NameId == nameKey).ToList();
                    var langKeys = nameItems.Select(r => (uint)r.LanguageId).Distinct().ToList();
                    var langDirOffset = (int)blob.Position;
                    var langEntryOffsets = WriteDirectoryWithEntries(blob, langKeys);

                    foreach (var langKey in langKeys)
                    {
                        var dataEntryOffset = (int)blob.Position;
                        blob.Write(new byte[ImageResourceDataEntry.SizeOfEntry]);
                        dataEntryOffsets.Add(dataEntryOffset);

                        // lang entry → DataEntry（OffsetToData = dataEntry 节内偏移，不置 DataIsDirectory）
                        var langIndex = langKeys.IndexOf(langKey);
                        PeBinary.WriteUInt32(blob.GetBuffer(), langEntryOffsets[langIndex] + 4, dataEntryOffset);
                    }

                    // name entry → lang dir（DataIsDirectory 位）
                    var nameIndex = nameKeys.IndexOf(nameKey);
                    PeBinary.WriteUInt32(blob.GetBuffer(), nameEntryOffsets[nameIndex] + 4, langDirOffset | unchecked((int)0x80000000));
                }

                // type entry → name dir（DataIsDirectory 位）
                var typeIndex = typeKeys.IndexOf(typeKey);
                PeBinary.WriteUInt32(blob.GetBuffer(), rootEntryOffsets[typeIndex] + 4, typeEntryOffset | unchecked((int)0x80000000));
            }

            // ---- 叶子数据区（追加末尾）----
            var dataOffsets = new List<int>();
            for (var r = 0; r < ordered.Count; r++)
            {
                dataOffsets.Add((int)blob.Position);
                blob.Write(ordered[r].Data, 0, ordered[r].Data.Length);
            }

            // ---- 回填 DataEntry：OffsetToData = 节内数据偏移；Size/CodePage/Reserved ----
            for (var r = 0; r < ordered.Count; r++)
            {
                PeBinary.WriteUInt32(blob.GetBuffer(), dataEntryOffsets[r] + 0, dataOffsets[r]);
                PeBinary.WriteUInt32(blob.GetBuffer(), dataEntryOffsets[r] + 4, ordered[r].Data.Length);
                PeBinary.WriteUInt32(blob.GetBuffer(), dataEntryOffsets[r] + 8, 0); // CodePage
                PeBinary.WriteUInt32(blob.GetBuffer(), dataEntryOffsets[r] + 12, 0); // Reserved
            }

            return new ResourceSectionResult(blob.ToArray(), dataEntryOffsets);
        }

        /// <summary>写一个目录头（16B）+ 各子项 entry（8B，Name=key 立即回填），返回各 entry 偏移。</summary>
        private static List<int> WriteDirectoryWithEntries(MemoryStream blob, IReadOnlyList<uint> keys)
        {
            var dirStart = (int)blob.Position;
            blob.Write(new byte[ImageResourceDirectory.SizeOfEntry]);
            PeBinary.WriteUInt16(blob.GetBuffer(), dirStart + 12, 0); // NamedEntries
            PeBinary.WriteUInt16(blob.GetBuffer(), dirStart + 14, (ushort)keys.Count); // IdEntries

            var entryOffsets = new List<int>(keys.Count);
            for (var i = 0; i < keys.Count; i++)
            {
                entryOffsets.Add((int)blob.Position);
                blob.Write(new byte[ImageResourceDirectoryEntry.SizeOfEntry]);
                PeBinary.WriteUInt32(blob.GetBuffer(), entryOffsets[i], (int)keys[i]);
            }

            return entryOffsets;
        }
    }

    /// <summary>.rsrc 节构建结果：完整数据 + 各 DataEntry.OffsetToData 字段的节内偏移。</summary>
    public sealed class ResourceSectionResult
    {
        public ResourceSectionResult(byte[] blob, IReadOnlyList<int> dataEntryOffsets)
        {
            Blob = blob;
            DataEntryOffsets = dataEntryOffsets;
        }

        public byte[] Blob { get; }
        public IReadOnlyList<int> DataEntryOffsets { get; }
    }
}