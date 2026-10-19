using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Cocoa.Targeting;

namespace Cocoa.CodeGen.ObjectFormat.PE
{
    /// <summary>
    /// PE 对象写出器：IObjectWriter 实现——把格式中立 ObjectWriteOptions 映射到 PeImageBuilder，
    /// 并回填 TimeDateStamp（COFF 头）与 CheckSum（可选头 CheckSum 字段，CheckSumMappedFile 语义）。
    /// 自举差分（PeImageDifferentialTests）对比 PeImageBuilder.Build 原始输出，本层回填不影响其判定。
    /// Rich 头（<see cref="FillRichHeader"/>）在此层插入：不触碰 PeImageBuilder，自举侧（PeImage.co）无需同步。
    /// </summary>
    public sealed class PeObjectWriter : IObjectWriter
    {
        /// <summary>PE 可选头 CheckSum 字段相对 PE 头的偏移（Signature4 + FileHeader20 + OptionalHeader 内 64）。</summary>
        private const int CheckSumFieldOffset = 4 + 20 + 64;

        public static readonly PeObjectWriter Instance = new();

        /// <summary>是否填充 TimeDateStamp（Unix 秒）。默认 true（CheckSum 真算 + 时间戳真填决策）。</summary>
        public bool FillTimeStamp { get; init; } = true;

        /// <summary>是否插入 Rich 头（仅 native 路径经此写出，默认开启，规避无 Rich 头的裸 PE 启发式判定）。</summary>
        public bool FillRichHeader { get; init; } = true;

        public byte[] Write(ObjectWriteOptions options)
        {
            var pe32 = options.Arch != Architecture.X64;
            var machine = options.Arch switch
            {
                Architecture.X64 => PeMachine.AMD64,
                _ => PeMachine.I386,
            };
            var imageBase = options.ImageBase;

            var config = new PeImageConfig(machine, imageBase, options.Subsystem, options.DllCharacteristics, options.AddressOfEntryPoint)
            {
                SectionAlignment = options.SectionAlignment,
                FileAlignment = options.FileAlignment,
                SizeOfHeaders = options.SizeOfHeaders,
                FileCharacteristicsOverride = options.FileCharacteristicsOverride,
            };

            var sections = new List<PeSectionSpec>(options.Sections.Count);
            foreach (var spec in options.Sections)
            {
                sections.Add(new PeSectionSpec(spec.Name, spec.RawData, spec.VirtualAddress, spec.Characteristics));
            }

            var directories = new List<(PeDataDirectoryEntry, uint, uint)>(options.Entries.Count);
            foreach (var entry in options.Entries)
            {
                directories.Add(((PeDataDirectoryEntry)entry.Entry, entry.Rva, entry.Size));
            }

            var image = PeImageBuilder.Build(config, sections, directories, options.AdditionalFileCharacteristics);

            // 先插入 Rich 头（更新 e_lfanew），再按新的 e_lfanew 回填 TimeDateStamp / CheckSum。
            if (FillRichHeader)
            {
                // 头区末端 = 节表末端（含全部节表项）；只搬 [旧 e_lfanew .. 节表末端)，绝不过界到节原始数据。
                var optSize = pe32 ? ImageOptionalHeader32.Size : ImageOptionalHeader64.Size;
                var headerEnd = RichHeaderBuilder.ELfanew + 4 + ImageFileHeader.Size + optSize + sections.Count * ImageSectionHeader.Size;
                RichHeaderBuilder.InsertInto(image, headerEnd, (int)options.SizeOfHeaders);
            }

            var eLfanew = PeBinary.ReadUInt32(image, 0x3C);

            // 回填 TimeDateStamp（COFF 头，e_lfanew+4+4）
            if (FillTimeStamp)
            {
                var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((int)(eLfanew + 8)), timestamp);
            }

            // COFF 符号表：追加到节原始数据之后，回填文件头 PointerToSymbolTable / NumberOfSymbols
            //（IDA/dumpbin/WinDbg 据此读函数符号 + 行号，无需 PDB 文件）。
            if (options.CoffSymbolTable != null && options.CoffSymbolTable.Length > 0)
            {
                var symbolFileOffset = (uint)image.Length;
                var withSymbols = new byte[image.Length + options.CoffSymbolTable.Length];
                image.CopyTo(withSymbols, 0);
                options.CoffSymbolTable.CopyTo(withSymbols, image.Length);
                image = withSymbols;

                // COFF 头内字段：Machine@0 NumberOfSections@2 TimeDateStamp@4 PointerToSymbolTable@8 NumberOfSymbols@12
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((int)(eLfanew + 4 + 8)), symbolFileOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((int)(eLfanew + 4 + 12)), (uint)options.CoffSymbolCount);
            }

            // 回填 CheckSum（可选头 CheckSum 字段；覆盖含 COFF 符号表的整镜像）
            var checksum = PeChecksum.Compute(image, (int)(eLfanew + CheckSumFieldOffset));
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((int)(eLfanew + CheckSumFieldOffset)), checksum);

            return image;
        }
    }
}
