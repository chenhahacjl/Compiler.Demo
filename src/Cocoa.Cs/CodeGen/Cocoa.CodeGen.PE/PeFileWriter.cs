using System;
using System.Collections.Generic;
using System.IO;
using Cocoa.Targeting;
using System.Linq;
using static Cocoa.CodeGen.PE.PeBinary;

namespace Cocoa.CodeGen.PE
{
    /// <summary>PE 写出器：组装头结构层 + 导入表结构层，产出 PE32+ / PE32 镜像文件。</summary>
    public static class PeFileWriter
    {
        public const int TextRva = 0x1000;
        public const int DataRva = 0x2000;
        public const int IdataRva = 0x5000;
        public const int SizeOfHeaders = 0x1000;

        private const int SectionAlignment = 0x1000;

        /// <summary>默认子系统：WindowsConsole（IMAGE_SUBSYSTEM_WINDOWS_CUI，3）。</summary>
        public const ushort PeSubsystemWindowsCui = 3;

        /// <summary>旧路径（RuntimeEmitter 手工布局）使用的固定数据段 RVA。新路径页对齐用 ComputeDataRva 动态布局。</summary>
        public static int ComputeDataRva(int codeLength)
        {
            return Align(TextRva + codeLength, SectionAlignment);
        }

        public static int ComputeIdataRva(int codeLength, int dataLength)
        {
            return Align(ComputeDataRva(codeLength) + dataLength, SectionAlignment);
        }

        public static long ImageBaseOf(Architecture architecture)
        {
            return architecture == Architecture.X86 ? 0x400000 : 0x140000000;
        }

        public static void Write(string outputPath, byte[] code, byte[] data, int entryPointRva, IReadOnlyList<PefileImport> imports, Architecture architecture, IReadOnlyList<int>? dataAbsoluteFixups = null, ushort subsystem = PeSubsystemWindowsCui, byte[]? cocoaMetadata = null, IReadOnlyList<PeExportEntry>? exports = null, byte[]? resources = null)
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
            uint cocoaRva = 0;
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

            // M4：.cocoa 元数据节（native 产物内嵌符号表）——紧随既有节之后布局，与其他节互不重叠。
            if (cocoaMetadata != null && cocoaMetadata.Length > 0)
            {
                var afterReloc = relocRva == 0 ? (uint)Align(idataRva + importLayout.Blob.Length, SectionAlignment) : relocRva + (uint)Align(relocBlob.Length, SectionAlignment);
                cocoaRva = afterReloc;
            }

            // C-3：.edata 导出表节（`[Export]` 函数 → 名字表 + 函数地址表），紧随 .cocoa 之后布局。
            uint edataRva = 0;
            byte[] exportBlob;
            if (exports != null && exports.Count > 0)
            {
                var afterCocoa = (uint)Align(idataRva + importLayout.Blob.Length, SectionAlignment);
                if (cocoaMetadata != null && cocoaMetadata.Length > 0)
                {
                    afterCocoa = cocoaRva + (uint)Align(cocoaMetadata.Length, SectionAlignment);
                }

                edataRva = afterCocoa;
                exportBlob = BuildExportTable(exports, edataRva);
            }
            else
            {
                exportBlob = Array.Empty<byte>();
            }

            // 各节虚拟末端（对齐后）必须恰好落在下一节起点（Windows 加载器按相邻节连续校验）。
            // .text 单一节（不做 4KB 分页）：分页会让节表随 code 增长而膨胀（>92 节将溢出固定头区且部分加载器拒载）。
            var sections = new List<PeSectionSpec>
            {
                new(".text", code, TextRva, PeSectionCharacteristics.Text),
            };

            sections.Add(new(".data", slotData, (uint)dataRva, PeSectionCharacteristics.Data));
            sections.Add(new(".idata", importLayout.Blob, (uint)idataRva, PeSectionCharacteristics.Data));
            if (relocBlob.Length > 0)
            {
                sections.Add(new(".reloc", relocBlob, relocRva, PeSectionCharacteristics.Data | PeSectionCharacteristics.MemDiscardable));
            }

            if (cocoaMetadata != null && cocoaMetadata.Length > 0)
            {
                sections.Add(new(".cocoa", cocoaMetadata, cocoaRva, PeSectionCharacteristics.Data | PeSectionCharacteristics.MemDiscardable));
            }

            if (exportBlob.Length > 0)
            {
                sections.Add(new(".edata", exportBlob, edataRva, PeSectionCharacteristics.Data));
                directories.Add((PeDataDirectoryEntry.Export, edataRva, (uint)exportBlob.Length));
            }

            // .rsrc 资源节（版本信息 VS_VERSIONINFO）：紧随 .edata 之后布局。资源 blob 内
            // DataEntry.OffsetToData（偏移 72）当前为镜像内偏移，须回填为 .rsrc 节 RVA + 数据偏移。
            uint rsrcRva = 0;
            byte[] resourceBlob = Array.Empty<byte>();
            if (resources != null && resources.Length > 0)
            {
                var afterEdata = (uint)Align(idataRva + importLayout.Blob.Length, SectionAlignment);
                if (exportBlob.Length > 0) afterEdata = edataRva + (uint)Align(exportBlob.Length, SectionAlignment);
                else if (cocoaMetadata != null && cocoaMetadata.Length > 0) afterEdata = cocoaRva + (uint)Align(cocoaMetadata.Length, SectionAlignment);
                else if (relocBlob.Length > 0) afterEdata = relocRva + (uint)Align(relocBlob.Length, SectionAlignment);

                rsrcRva = afterEdata;
                resourceBlob = (byte[])resources.Clone();
                // DataEntry 内相对数据区偏移固定为 88（见 VersionResourceBuilder）
                var dataRvaInRsrc = (int)(rsrcRva + 88);
                PeBinary.WriteUInt32(resourceBlob, 72, dataRvaInRsrc);
                directories.Add((PeDataDirectoryEntry.Resource, rsrcRva, (uint)resourceBlob.Length));
                sections.Add(new(".rsrc", resourceBlob, rsrcRva, PeSectionCharacteristics.Data));
            }

            // 原生镜像：.text 按 4KB 页分页成多个节（.text/.textN），节表须容纳全部节头（每节 40 字节）。
            // 固定 0x1000 在 code 跨页多节（>92 节）时越界——按节数动态抬升，仍保证 ≥ 节对齐。
            var sectionTableOffset = PeConstants.DosHeaderSize + PeImageBuilder.DosStubSize + 4 + ImageFileHeader.Size;
            var optionalHeaderSize = pe32 ? ImageOptionalHeader32.Size : ImageOptionalHeader64.Size;
            var minHeaders = Align(sectionTableOffset + optionalHeaderSize + sections.Count * ImageSectionHeader.Size, 0x200);
            // SizeOfHeaders 须同时 ≥ 节表、(建议)节对齐倍数，否则加载器可能拒载。
            var headersSize = Math.Max(SizeOfHeaders, Align(minHeaders, SectionAlignment));

            var config = new PeImageConfig(
                pe32 ? PeMachine.I386 : PeMachine.AMD64,
                (ulong)ImageBaseOf(architecture),
                (ushort)subsystem,
                pe32 ? (ushort)(PeDllCharacteristics.NxChipCompat | PeDllCharacteristics.NoSeh | PeDllCharacteristics.TerminalServerAware)
                     : (ushort)(PeDllCharacteristics.CurrentImage | PeDllCharacteristics.TerminalServerAware),
                (uint)entryPointRva)
            {
                SizeOfHeaders = (uint)headersSize,
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

        /// <summary>
        /// C-3：构建 .edata 导出表 blob（IMAGE_EXPORT_DIRECTORY + 名字表 + ordinal 表 + 函数地址表 + 名字字符串）。
        /// 名字按 ord 排列以保证名字表与 ordinal 表一一对应；序号 = 每条目的索引（ordinal base 1）。
        /// forwarder 不做（本编译器无转发导出）。
        /// </summary>
        private static byte[] BuildExportTable(IReadOnlyList<PeExportEntry> exports, uint edataRva)
        {
            var count = exports.Count;

            // 布局（单位字节；blob 内相对偏移）：
            //   [0 .. 40)         IMAGE_EXPORT_DIRECTORY
            //   [40 .. 40+4N)     名字表（每个名字字符串 RVA）
            //   [40+4N .. 40+6N)  ordinal 表（ushort：函数表索引）
            //   [40+6N .. 40+10N) 函数地址表（每个函数 RVA）
            //   [40+10N ..)       名字字符串（".edata 区末尾追加，供名字表引用"）
            var namesTableOffset = 40;
            var ordinalsTableOffset = 40 + 4 * count;
            var functionsTableOffset = 40 + 6 * count;
            var stringsOffset = 40 + 10 * count;

            var blob = new List<byte>(64 + stringsOffset + count * 24);
            for (var i = 0; i < stringsOffset; i++)
            {
                blob.Add(0);
            }

            // 名字字符串区：先写字符串，记录其相对偏移 → 再回填名字表
            var nameRvas = new uint[count];
            var nameOffsets = new int[count];
            for (var i = 0; i < count; i++)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(exports[i].Name + "\0");
                nameOffsets[i] = blob.Count;
                for (var b = 0; b < bytes.Length; b++)
                {
                    blob.Add(bytes[b]);
                }
            }

            for (var i = 0; i < count; i++)
            {
                nameRvas[i] = edataRva + (uint)(stringsOffset + (nameOffsets[i] - stringsOffset));
            }

            // 名字表（Absolute RVA）
            for (var i = 0; i < count; i++)
            {
                WriteUInt32Value(blob, (int)namesTableOffset + i * 4, nameRvas[i]);
            }

            // ordinal 表（函数表索引，0..N-1）
            for (var i = 0; i < count; i++)
            {
                WriteUInt16Value(blob, ordinalsTableOffset + i * 2, (ushort)i);
            }

            // 函数地址表（函数 RVA）
            for (var i = 0; i < count; i++)
            {
                WriteUInt32Value(blob, functionsTableOffset + i * 4, exports[i].Rva);
            }

            // 目录头：Name 字段 = 模块名前缀串（供工具显示），Base=1
            var dllNameOffset = blob.Count;
            foreach (var ch in "cocoa" + "\0")
            {
                blob.Add((byte)ch);
            }

            var dir = new ImageExportDirectory(
                Characteristics: 0,
                TimeDateStamp: 0,
                MajorVersion: 0,
                MinorVersion: 0,
                Name: edataRva + (uint)dllNameOffset,
                Base: 1,
                NumberOfFunctions: (uint)count,
                NumberOfNames: (uint)count,
                AddressOfFunctions: edataRva + (uint)functionsTableOffset,
                AddressOfNames: edataRva + (uint)namesTableOffset,
                AddressOfNameOrdinals: edataRva + (uint)ordinalsTableOffset);
            var header = new byte[ImageExportDirectory.SizeOfEntry];
            dir.Write(header);
            for (var i = 0; i < header.Length; i++)
            {
                blob[i] = header[i];
            }

            return blob.ToArray();
        }

        private static void WriteUInt32Value(List<byte> blob, int offset, uint value)
        {
            blob[offset] = (byte)value;
            blob[offset + 1] = (byte)(value >> 8);
            blob[offset + 2] = (byte)(value >> 16);
            blob[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteUInt16Value(List<byte> blob, int offset, ushort value)
        {
            blob[offset] = (byte)value;
            blob[offset + 1] = (byte)(value >> 8);
        }

    }
}
