using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Cocoa.CodeAnalysis.Emit.Native.PEFile
{
    /// <summary>导入规格：DLL 名 + 函数名 + IAT 槽在 .idata blob 内的偏移。</summary>
    internal readonly record struct PefileImport(string DllName, string Name, int IatOffset);

    /// <summary>PE 写出器：组装头结构层 + 导入表结构层，产出 PE32+ / PE32 镜像文件。</summary>
    internal static class PefileWriter
    {
        public const int TextRva = 0x1000;
        public const int DataRva = 0x2000;
        public const int IdataRva = 0x5000;
        public const int SizeOfHeaders = 0x400;

        private const int SectionAlignment = 0x1000;

        /// <summary>旧路径（RuntimeEmitter 手工布局）使用的固定数据段 RVA。新路径须用 ComputeDataRva 动态布局。</summary>
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

        public static void Write(string outputPath, byte[] code, byte[] data, int entryPointRva, IReadOnlyList<PefileImport> imports, Architecture architecture)
        {
            var pe32 = architecture == Architecture.X86;

            var dataRva = ComputeDataRva(code.Length);
            var idataRva = ComputeIdataRva(code.Length, data.Length);

            var specs = new List<PeImportSpec>();
            foreach (var import in imports)
            {
                specs.Add(new PeImportSpec(import.DllName, import.Name));
            }

            var importLayout = ImportTableBuilder.Build(specs, (uint)idataRva, pe32);

            var directories = new List<(PeDataDirectoryEntry Entry, uint Rva, uint Size)>();
            if (imports.Count > 0)
            {
                var iatEntrySize = pe32 ? ImageThunkData32.SizeOfEntry : ImageThunkData64.SizeOfEntry;
                var iatSlotCount = importLayout.Dlls.Sum(d => d.Entries.Count);
                directories.Add((PeDataDirectoryEntry.Import, (uint)idataRva + (uint)importLayout.DescriptorsOffset, (uint)((importLayout.Dlls.Count + 1) * ImageImportDescriptor.SizeOfEntry)));
                directories.Add((PeDataDirectoryEntry.Iat, (uint)idataRva + (uint)importLayout.Dlls[0].IatOffset, (uint)(iatSlotCount * iatEntrySize)));
            }

            // 可执行节虚拟大小不得超过 SectionAlignment（0x1000），故代码按页拆分为多个 .text 节；
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

            sections.Add(new(".data", data, (uint)dataRva, PeSectionCharacteristics.Data));
            sections.Add(new(".idata", importLayout.Blob, (uint)idataRva, PeSectionCharacteristics.Data));

            var config = new PeImageConfig(
                pe32 ? PeMachine.I386 : PeMachine.AMD64,
                (ulong)ImageBaseOf(architecture),
                (ushort)PeSubsystem.WindowsCui,
                pe32 ? (ushort)(PeDllCharacteristics.NxChipCompat | PeDllCharacteristics.NoSeh | PeDllCharacteristics.TerminalServerAware)
                     : (ushort)(PeDllCharacteristics.CurrentImage | PeDllCharacteristics.TerminalServerAware),
                (uint)entryPointRva);

            var image = PeImageBuilder.Build(config, sections, directories, pe32 ? PeFileCharacteristics.RelocsStripped : (ushort)0);
            File.WriteAllBytes(outputPath, image);
        }
    }
}