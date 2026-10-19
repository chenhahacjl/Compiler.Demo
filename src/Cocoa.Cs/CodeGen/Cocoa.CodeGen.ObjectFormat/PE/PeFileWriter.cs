using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Cocoa.Targeting;
using System.Linq;
using static Cocoa.CodeGen.ObjectFormat.PE.PeBinary;

namespace Cocoa.CodeGen.ObjectFormat.PE
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

        /// <summary>
        /// 构建自包含 `.idata` 导入表布局（descriptors + INT + hint/name + 内嵌 IAT），blobBaseRva=idataRva。
        /// 供 NativeCodeEmitter/EmitNativeLibrary 在建 layout 后把各槽偏移交给汇编器 Patch 并传给 PeFileWriter。
        /// </summary>
        /// <param name="zeroIatArray">true = IAT 数组写 0（csc 形态）。实测（2026-10-19）：0 槽在 .idata 同样被
        /// Windows 加载器按 bound-import 跳过回填（26/26 native e2e 崩 0xC0000005），故默认 false = 镜像 INT
        /// （Borland/老 VS "thunk mirror in .idata" 形态，加载器 verify-then-replace 依赖非零槽、IDA 按常规导入识别）。</param>
        public static (PeImportTableLayout Layout, int IdataRva) BuildImportLayout(
            IReadOnlyList<PeFileImport> imports, Architecture architecture, int codeLength, int dataLength, bool zeroIatArray = false)
        {
            var pe32 = architecture == Architecture.X86;
            var idataRva = ComputeIdataRva(codeLength, dataLength);
            var specs = new List<PeImportSpec>(imports.Count);
            foreach (var import in imports)
            {
                specs.Add(new PeImportSpec(import.DllName, import.Name));
            }

            var layout = ImportTableBuilder.Build(specs, (uint)idataRva, pe32, externalIatOffsets: null, externalIatRva: 0, zeroIatArray);
            return (layout, idataRva);
        }

        public static long ImageBaseOf(Architecture architecture)
        {
            return architecture == Architecture.X86 ? 0x400000 : 0x140000000;
        }

        public static void Write(string outputPath, byte[] code, byte[] data, int entryPointRva, PeImportTableLayout? importLayout, Architecture architecture, IReadOnlyList<int>? dataAbsoluteFixups = null, ushort subsystem = PeSubsystemWindowsCui, byte[]? cocoaMetadata = null, IReadOnlyList<PeExportEntry>? exports = null, ResourceSectionResult? resources = null, bool isDll = false, int cocoaMetadataPtrDataOffset = -1, byte[]? coffSymbolTable = null, int coffSymbolCount = 0, string? pdbPath = null, Guid? pdbGuid = null)
        {
            var pe32 = architecture == Architecture.X86;

            var dataRva = ComputeDataRva(code.Length);
            var idataRva = ComputeIdataRva(code.Length, data.Length);

            // .data 副本：供 .cocoa 元数据指针槽回填（IAT 已迁入 .idata，不再写 .data）。
            var slotData = new byte[data.Length];
            Array.Copy(data, slotData, data.Length);

            var directories = new List<(PeDataDirectoryEntry Entry, uint Rva, uint Size)>();
            if (importLayout != null && importLayout.Dlls.Count > 0)
            {
                var iatEntrySize = pe32 ? ImageThunkData32.SizeOfEntry : ImageThunkData64.SizeOfEntry;
                var iatSlotCount = importLayout.Dlls.Sum(d => d.Entries.Count);
                var firstIatRva = (uint)idataRva + (uint)importLayout.Dlls[0].IatOffset;
                directories.Add((PeDataDirectoryEntry.Import, (uint)idataRva + (uint)importLayout.DescriptorsOffset, (uint)((importLayout.Dlls.Count + 1) * ImageImportDescriptor.SizeOfEntry)));
                directories.Add((PeDataDirectoryEntry.Iat, firstIatRva, (uint)(iatSlotCount * iatEntrySize)));
            }

            // M4a：基址重定位（.reloc）——数据段内绝对地址槽（vtable 函数/名字指针）在镜像
            // 被加载器重定位（ASLR/DYNAMIC_BASE）时必须同步修正，否则间接调用跳到旧 VA 崩溃。
            // x64 用 DIR64、x86 用 HIGHLOW；无重定位项时不发节（保持 RelocsStripped 现状）。
            var relocOffsets = dataAbsoluteFixups != null ? dataAbsoluteFixups.Distinct().OrderBy(x => x).ToList() : new List<int>();
            // M6（弱化）：.cocoa 元数据指针槽位于 .data，绝对 VA 需随镜像重定位（ASLR）。
            if (cocoaMetadataPtrDataOffset >= 0 && cocoaMetadata != null && cocoaMetadata.Length > 0)
            {
                relocOffsets.Add(cocoaMetadataPtrDataOffset);
                relocOffsets = relocOffsets.Distinct().OrderBy(x => x).ToList();
            }

            byte[] relocBlob;
            var relocRva = 0u;
            uint cocoaRva = 0;
            if (relocOffsets.Count > 0)
            {
                relocBlob = BuildRelocBlob(relocOffsets, (uint)dataRva, pe32);
            }
            else
            {
                relocBlob = Array.Empty<byte>();
            }

            // 节游标：从 .idata 虚拟末端（对齐）起，按最终节序连续布局；.cocoa 置于末位。
            uint cursor = (uint)Align(idataRva + (importLayout?.Blob.Length ?? 0), SectionAlignment);

            // ---- .reloc ----
            if (relocBlob.Length > 0)
            {
                relocRva = cursor;
                cursor = relocRva + (uint)Align(relocBlob.Length, SectionAlignment);
                directories.Add((PeDataDirectoryEntry.BaseReloc, relocRva, (uint)relocBlob.Length));
            }

            // ---- .edata 导出表（`[Export]` 函数 → 名字表 + 函数地址表）----
            uint edataRva = 0;
            byte[] exportBlob;
            if (exports != null && exports.Count > 0)
            {
                edataRva = cursor;
                exportBlob = BuildExportTable(exports, edataRva);
                cursor = edataRva + (uint)Align(exportBlob.Length, SectionAlignment);
            }
            else
            {
                exportBlob = Array.Empty<byte>();
            }

            // ---- .rsrc 资源节（版本信息/图标/通用资源）----
            uint rsrcRva = 0;
            byte[] resourceBlob = Array.Empty<byte>();
            if (resources != null && resources.Blob.Length > 0)
            {
                rsrcRva = cursor;
                resourceBlob = (byte[])resources.Blob.Clone();
                // 每个 DataEntry.OffsetToData：节内偏移 → .rsrc 节 RVA + 数据偏移
                foreach (var dataEntryOffset in resources.DataEntryOffsets)
                {
                    var dataOffsetInBlob = (int)PeBinary.ReadUInt32(resourceBlob, dataEntryOffset);
                    PeBinary.WriteUInt32(resourceBlob, dataEntryOffset, (int)(rsrcRva + (uint)dataOffsetInBlob));
                }

                cursor = rsrcRva + (uint)Align(resourceBlob.Length, SectionAlignment);
                directories.Add((PeDataDirectoryEntry.Resource, rsrcRva, (uint)resourceBlob.Length));
            }

            // ---- .rdata（Debug 目录 + CodeView RSDS → 真实 .pdb）----
            // 仅当外部生成了实际 .pdb 时嵌入（避免广告不存在的文件）。
            int rdataIndex = -1;
            uint rdataRva = 0;
            byte[] debugBlob = Array.Empty<byte>();
            if (pdbPath != null && pdbGuid != null)
            {
                rdataRva = cursor;
                var pdbPathBytes = Encoding.UTF8.GetBytes(pdbPath);
                var rsds = new PeCodeViewRsds(PeCodeViewRsds.RsdsSignature, pdbGuid.Value, 1, pdbPathBytes);
                var rsdsBlob = new byte[rsds.Size];
                rsds.Write(rsdsBlob);

                var debugDir = new ImageDebugDirectory(
                    Characteristics: 0,
                    TimeDateStamp: 0,
                    MajorVersion: 0,
                    MinorVersion: 0,
                    Type: PeDebugType.CodeView,
                    SizeOfData: (uint)rsdsBlob.Length,
                    AddressOfRawData: rdataRva + (uint)ImageDebugDirectory.SizeOfEntry,
                    PointerToRawData: 0); // 文件偏移依赖 headersSize，节表定稿后回填
                var debugDirBlob = new byte[ImageDebugDirectory.SizeOfEntry];
                debugDir.Write(debugDirBlob);

                debugBlob = new byte[ImageDebugDirectory.SizeOfEntry + rsdsBlob.Length];
                Array.Copy(debugDirBlob, debugBlob, ImageDebugDirectory.SizeOfEntry);
                Array.Copy(rsdsBlob, 0, debugBlob, ImageDebugDirectory.SizeOfEntry, rsdsBlob.Length);

                cursor = rdataRva + (uint)Align(debugBlob.Length, SectionAlignment);
            }

            // ---- .cocoa 元数据节（native 产物内嵌符号表）——置于末位 ----
            if (cocoaMetadata != null && cocoaMetadata.Length > 0)
            {
                cocoaRva = cursor;

                // M6（弱化）：回填 .cocoa 元数据绝对 VA（ASLR 经上面 .reloc 重定位）。
                if (cocoaMetadataPtrDataOffset >= 0)
                {
                    var metadataPtr = (ulong)ImageBaseOf(architecture) + (uint)cocoaRva;
                    if (pe32)
                    {
                        WriteUInt32(slotData, cocoaMetadataPtrDataOffset, (int)metadataPtr);
                    }
                    else
                    {
                        WriteUInt64(slotData, cocoaMetadataPtrDataOffset, (long)metadataPtr);
                    }
                }
            }

            // 各节虚拟末端（对齐后）必须恰好落在下一节起点（Windows 加载器按相邻节连续校验）。
            // 最终顺序：.text/.data/.idata/.reloc/.edata/.rsrc/.rdata/.cocoa。
            var sections = new List<PeSectionSpec>
            {
                new(".text", code, TextRva, PeSectionCharacteristics.Text),
                new(".data", slotData, (uint)dataRva, PeSectionCharacteristics.Data),
            };
            if (importLayout != null && importLayout.Blob.Length > 0)
            {
                sections.Add(new(".idata", importLayout.Blob, (uint)idataRva, PeSectionCharacteristics.Data));
            }

            if (relocBlob.Length > 0)
            {
                sections.Add(new(".reloc", relocBlob, relocRva, PeSectionCharacteristics.Data | PeSectionCharacteristics.MemDiscardable));
            }

            if (exportBlob.Length > 0)
            {
                sections.Add(new(".edata", exportBlob, edataRva, PeSectionCharacteristics.Data));
                directories.Add((PeDataDirectoryEntry.Export, edataRva, (uint)exportBlob.Length));
            }

            if (resources != null && resourceBlob.Length > 0)
            {
                sections.Add(new(".rsrc", resourceBlob, rsrcRva, PeSectionCharacteristics.Data));
            }

            if (pdbPath != null && pdbGuid != null && debugBlob.Length > 0)
            {
                rdataIndex = sections.Count;
                sections.Add(new(".rdata", debugBlob, rdataRva, PeSectionCharacteristics.Data));
            }

            if (cocoaMetadata != null && cocoaMetadata.Length > 0)
            {
                // .cocoa 常驻（去 MemDiscardable），置于末位。
                sections.Add(new(".cocoa", cocoaMetadata, cocoaRva, PeSectionCharacteristics.Data));
            }

            // 原生镜像：.text 按 4KB 页分页成多个节（.text/.textN），节表须容纳全部节头（每节 40 字节）。
            // 固定 0x1000 在 code 跨页多节（>92 节）时越界——按节数动态抬升，仍保证 ≥ 节对齐。
            // SizeOfHeaders 须覆盖 Rich 头插入后的节表位置（e_lfanew 由 0x80 抬升至 0x100）。
            var sectionTableOffset = RichHeaderBuilder.ELfanew + 4 + ImageFileHeader.Size;
            var optionalHeaderSize = pe32 ? ImageOptionalHeader32.Size : ImageOptionalHeader64.Size;
            var minHeaders = Align(sectionTableOffset + optionalHeaderSize + sections.Count * ImageSectionHeader.Size, 0x200);
            // SizeOfHeaders 须同时 ≥ 节表、(建议)节对齐倍数，否则加载器可能拒载。
            var headersSize = Math.Max(SizeOfHeaders, Align(minHeaders, SectionAlignment));

            // 回填 Debug 目录 RSDS 的 PointerToRawData（文件偏移 = headersSize + 前序各节对齐后的原始长度，
            // 至 .rdata 自身为止，与 PeObjectWriter 的 raw 布局算法一致，FileAlignment=0x200）。
            if (pdbPath != null && pdbGuid != null && debugBlob.Length > 0)
            {
                var debugFileOff = (uint)headersSize;
                for (var i = 0; i < rdataIndex; i++)
                {
                    debugFileOff += Align((uint)sections[i].RawData.Length, 0x200);
                }

                PeBinary.WriteUInt32(debugBlob, 24, (int)(debugFileOff + ImageDebugDirectory.SizeOfEntry));
                directories.Add((PeDataDirectoryEntry.Debug, rdataRva, (uint)ImageDebugDirectory.SizeOfEntry));
            }

            var dllCharacteristics = pe32
                ? (ushort)(PeDllCharacteristics.NxChipCompat | PeDllCharacteristics.NoSeh | PeDllCharacteristics.TerminalServerAware)
                : (ushort)(PeDllCharacteristics.CurrentImage | PeDllCharacteristics.TerminalServerAware);

            // 有重定位节时不再声明 RelocsStripped（x86 历史行为保留：无重定位时仍声明剥离）
            var fileCharacteristics = relocBlob.Length > 0 ? (ushort)0 : (pe32 ? PeFileCharacteristics.RelocsStripped : (ushort)0);

            // T4：DLL 形态（cocoa runtime / System.Core.Native 等共享库）——entry=0、FileCharacteristicsOverride
            // 强制 CurrentImage|Dll（0x2022，PeImageBuilder 仅在 Override=0 时才回退 ExecutableImage），
            // 使加载器按 DLL 语义映射（无入口、可被 app 导入）
            var effectiveEntry = entryPointRva;
            if (isDll)
            {
                effectiveEntry = 0;
                // AdditionalFileCharacteristics=Dll → PeImageBuilder 回退 = CurrentImage|Dll（0x2022）
                fileCharacteristics = PeFileCharacteristics.Dll;
            }

            // T3：经 PeObjectWriter 写出——回填 TimeDateStamp + CheckSum（CheckSumMappedFile 语义）
            var objOptions = new Cocoa.Targeting.ObjectWriteOptions(
                Cocoa.Targeting.TargetOS.Windows,
                pe32 ? Cocoa.Targeting.Architecture.X86 : Cocoa.Targeting.Architecture.X64,
                (ulong)ImageBaseOf(architecture),
                (ushort)subsystem,
                (uint)effectiveEntry,
                SectionAlignment, 0x200)
            {
                SizeOfHeaders = (uint)headersSize,
                DllCharacteristics = dllCharacteristics,
                AdditionalFileCharacteristics = fileCharacteristics,
                CoffSymbolTable = coffSymbolTable,
                CoffSymbolCount = coffSymbolCount,
            };
            foreach (var section in sections)
            {
                objOptions.Sections.Add(new Cocoa.Targeting.SectionSpec(section.Name, section.RawData, section.VirtualAddress, section.Characteristics));
            }

            foreach (var (entry, rva, size) in directories)
            {
                objOptions.Entries.Add(new Cocoa.Targeting.TargetEntry((int)entry, rva, size));
            }

            var image = PeObjectWriter.Instance.Write(objOptions);
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
        /// **名字表必须按名字升序**（PE/COFF 规范 + Windows 加载器 LdrpSearchExportDirectory 对名字表做二分查找，
        /// 乱序将导致 GetProcAddress 大多数名字解析失败）——先按 Name 排序再构建，序号 = 排序后的条目索引（ordinal base 1）。
        /// forwarder 不做（本编译器无转发导出）。
        /// </summary>
        private static byte[] BuildExportTable(IReadOnlyList<PeExportEntry> exports, uint edataRva)
        {
            var ordered = exports.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
            var count = ordered.Count;

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
                var bytes = System.Text.Encoding.UTF8.GetBytes(ordered[i].Name + "\0");
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
                WriteUInt32Value(blob, functionsTableOffset + i * 4, ordered[i].Rva);
            }

            // 目录头：Name 字段 = 模块名前缀串（供工具显示），Base=1
            var dllNameOffset = blob.Count;
            foreach (var ch in "Cocoa" + "\0")
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

