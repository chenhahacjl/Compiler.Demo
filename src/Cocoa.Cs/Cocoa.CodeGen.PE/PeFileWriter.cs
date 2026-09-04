using System;
using System.Collections.Generic;
using System.IO;
using Cocoa.Targeting;
using System.Linq;

namespace Cocoa.CodeGen.PE
{
    /// <summary>导入规格：DLL 名 + 函数名 + IAT 槽在 .idata blob 内的偏移。</summary>
    internal readonly record struct PefileImport(string DllName, string Name, int IatOffset);

    /// <summary>PE 写出器：组装头结构层 + 导入表结构层，产出 PE32+ / PE32 镜像文件。</summary>
    internal static class PeFileWriter
    {
        public const int TextRva = 0x1000;
        public const int DataRva = 0x2000;
        public const int IdataRva = 0x5000;
        public const int SizeOfHeaders = 0x1000;

        private const int SectionAlignment = 0x1000;

        /// <summary>旧路径（RuntimeEmitter 手工布局）使用的固定数据段 RVA。新路径页对齐用 ComputeDataRva 动态布局。</summary>
        public static int ComputeDataRva(int codeLength)
        {
            return Align(TextRva + codeLength, SectionAlignment);
        }

        public static int ComputeIdataRva(int codeLength, int dataLength)
        {
            return Align(ComputeDataRva(codeLength) + dataLength, SectionAlignment);
        }

        private static int Align(int value, int alignment)
        {
            var remainder = value % alignment;
            return remainder == 0 ? value : value + alignment - remainder;
        }

        public static long ImageBaseOf(Architecture architecture)
        {
            return architecture == Architecture.X86 ? 0x400000 : 0x140000000;
        }

        public static void Write(string outputPath, byte[] code, byte[] data, int entryPointRva, IReadOnlyList<PefileImport> imports, Architecture architecture, IReadOnlyList<int>? dataAbsoluteFixups = null)
        {
            var pe32 = architecture == Architecture.X86;

            var dataRva = ComputeDataRva(code.Length);
            var idataRva = ComputeIdataRva(code.Length, data.Length);

            var specs = new List<PeImportSpec>();
            foreach (var import in imports)
            {
                specs.Add(new PeImportSpec(import.DllName, import.Name));
            }

            var slotOffsets = imports.Select(i => i.IatOffset).ToList();
            var importLayout = ImportTableBuilder.Build(specs, (uint)idataRva, pe32, slotOffsets, (uint)dataRva);

            // 6c-2：IAT 槽磁盘初值 = hintname RVA（mingw fake-IAT 惯例）。Windows 加载器对
            // 初值为 0 的槽视为已填充而跳过（bound-import 语义），只有指向 INT 区间的"伪值"
            // 才会被替换为解析后的真实函数地址，故槽不能留空。
            var slotData = new byte[data.Length];
            Array.Copy(data, slotData, data.Length);
            foreach (var dll in importLayout.Dlls)
            {
                for (var e = 0; e < dll.Entries.Count; e++)
                {
                    var entry = dll.Entries[e];
                    var import = imports.First(i =>
                        string.Equals(i.DllName, entry.Spec.DllName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(i.Name, entry.Spec.FunctionName, StringComparison.OrdinalIgnoreCase));
                    var hintNameRva = (uint)idataRva + (uint)entry.HintNameOffset;
                    if (pe32)
                    {
                        WriteUInt32(slotData, import.IatOffset, (int)hintNameRva);
                    }
                    else
                    {
                        WriteUInt64(slotData, import.IatOffset, hintNameRva);
                    }
                }
            }

            var directories = new List<(PeDataDirectoryEntry Entry, uint Rva, uint Size)>();
            if (imports.Count > 0)
            {
                var iatEntrySize = pe32 ? ImageThunkData32.SizeOfEntry : ImageThunkData64.SizeOfEntry;
                var iatSlotCount = importLayout.Dlls.Sum(d => d.Entries.Count);
                var firstIatRva = (uint)dataRva + (uint)importLayout.Dlls[0].IatOffset;
                directories.Add((PeDataDirectoryEntry.Import, (uint)idataRva + (uint)importLayout.DescriptorsOffset, (uint)((importLayout.Dlls.Count + 1) * ImageImportDescriptor.SizeOfEntry)));
                directories.Add((PeDataDirectoryEntry.Iat, firstIatRva, (uint)(iatSlotCount * iatEntrySize)));
            }

            // M4a：基址重定位（.reloc）——数据段内绝对地址槽（vtable 函数/名字指针）在镜像
            // 被加载器重定位（ASLR/DYNAMIC_BASE）时必须同步修正，否则间接调用跳到旧 VA 崩溃。
            // x64 用 DIR64、x86 用 HIGHLOW；无重定位项时不发节（保持 RelocsStripped 现状）。
            var relocOffsets = dataAbsoluteFixups != null ? dataAbsoluteFixups.Distinct().OrderBy(x => x).ToList() : new List<int>();
            byte[] relocBlob;
            var relocRva = 0u;
            if (relocOffsets.Count > 0)
            {
                relocRva = (uint)Align(idataRva + importLayout.Blob.Length, SectionAlignment);
                relocBlob = BuildRelocBlob(relocOffsets, (uint)dataRva, pe32);
                directories.Add((PeDataDirectoryEntry.BaseReloc, relocRva, (uint)relocBlob.Length));
            }
            else
            {
                relocBlob = Array.Empty<byte>();
            }

            // 才会被替换为解析后的真实函数地址，故槽不能留空。
            // 各节虚拟末端（对齐后）必须恰好落在下一节起点（Windows 加载器按相邻节连续校验）。
            var sections = new List<PeSectionSpec>();
            var codeOffset = 0;
            var codeSectionIndex = 0;
            while (codeOffset < code.Length)
            {
                var chunkLength = Math.Min(SectionAlignment, code.Length - codeOffset);
                var chunk = new byte[chunkLength];
                Array.Copy(code, codeOffset, chunk, 0, chunkLength);
                var name = codeSectionIndex == 0 ? ".text" : ".text" + codeSectionIndex.ToString();
                sections.Add(new PeSectionSpec(name, chunk, (uint)(TextRva + codeOffset), PeSectionCharacteristics.Text));
                codeOffset += chunkLength;
                codeSectionIndex++;
            }

            sections.Add(new(".data", slotData, (uint)dataRva, PeSectionCharacteristics.Data));
            sections.Add(new(".idata", importLayout.Blob, (uint)idataRva, PeSectionCharacteristics.Data));
            if (relocBlob.Length > 0)
            {
                sections.Add(new(".reloc", relocBlob, relocRva, PeSectionCharacteristics.Data | PeSectionCharacteristics.MemDiscardable));
            }

            var config = new PeImageConfig(
                pe32 ? PeMachine.I386 : PeMachine.AMD64,
                (ulong)ImageBaseOf(architecture),
                (ushort)PeSubsystem.WindowsCui,
                pe32 ? (ushort)(PeDllCharacteristics.NxChipCompat | PeDllCharacteristics.NoSeh | PeDllCharacteristics.TerminalServerAware)
                     : (ushort)(PeDllCharacteristics.CurrentImage | PeDllCharacteristics.TerminalServerAware),
                (uint)entryPointRva)
            {
                SizeOfHeaders = (uint)SizeOfHeaders,
            };

            // 有重定位节时不再声明 RelocsStripped（x86 历史行为保留：无重定位时仍声明剥离）
            var fileCharacteristics = relocBlob.Length > 0 ? (ushort)0 : (pe32 ? PeFileCharacteristics.RelocsStripped : (ushort)0);
            var image = PeImageBuilder.Build(config, sections, directories, fileCharacteristics);
            File.WriteAllBytes(outputPath, image);
        }

        /// <summary>M4a：按 4KB 页分组生成 .reloc blob（块头 PageRva+SizeOfBlock + TypeOffset WORD 数组，奇数项补 ABSOLUTE 对齐到 4 字节）。</summary>
        private static byte[] BuildRelocBlob(IReadOnlyList<int> offsets, uint dataRva, bool pe32)
        {
            var entryType = pe32 ? PeRelocType.HighLow : PeRelocType.Dir64;
            var blocks = new List<(uint PageRva, List<ushort> Entries)>();

            foreach (var offset in offsets)
            {
                var rva = (uint)(dataRva + offset);
                var page = rva & ~0xFFFu;
                if (blocks.Count == 0 || blocks[^1].PageRva != page)
                {
                    blocks.Add((page, new List<ushort>()));
                }

                blocks[^1].Entries.Add((ushort)(((int)entryType << 12) | (int)(rva & 0xFFF)));
            }

            var blob = new List<byte>();
            foreach (var (pageRva, entries) in blocks)
            {
                if (entries.Count % 2 != 0)
                {
                    entries.Add(0); // IMAGE_REL_BASED_ABSOLUTE 填充，保持块 4 字节对齐
                }

                var sizeOfBlock = 8 + 2 * entries.Count;
                WriteUInt32Into(blob, pageRva);
                WriteUInt32Into(blob, (uint)sizeOfBlock);
                foreach (var entry in entries)
                {
                    WriteUInt16Into(blob, entry);
                }
            }

            return blob.ToArray();
        }

        private static void WriteUInt32Into(List<byte> blob, uint value)
        {
            blob.Add((byte)value);
            blob.Add((byte)(value >> 8));
            blob.Add((byte)(value >> 16));
            blob.Add((byte)(value >> 24));
        }

        private static void WriteUInt16Into(List<byte> blob, ushort value)
        {
            blob.Add((byte)value);
            blob.Add((byte)(value >> 8));
        }

        private static void WriteUInt32(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteUInt64(byte[] bytes, int offset, long value)
        {
            for (var i = 0; i < 8; i++)
            {
                bytes[offset + i] = (byte)(value >> (i * 8));
            }
        }
    }
}