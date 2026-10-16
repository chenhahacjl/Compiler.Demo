using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Cocoa.Targeting;

namespace Cocoa.CodeGen.Elf
{
    /// <summary>ELF64 头尺寸。</summary>
    internal static class ElfConstants
    {
        public const int HeaderSize = 64;
        public const int ProgramHeaderSize = 56;
        public const int SectionHeaderSize = 64;

        // e_ident
        public const byte Elf64Class = 2;   // ELFCLASS64
        public const byte LittleEndian = 1; // ELFDATA2LSB
        public const byte SysVAbi = 0;

        // e_type
        public const ushort EtExec = 2;

        // e_machine
        public const ushort EmX86_64 = 0x3E;

        // p_type
        public const uint PtLoad = 1;
        public const uint PtGnuStack = 0x6474E551;

        // p_flags
        public const uint PfX = 1;
        public const uint PfW = 2;
        public const uint PfR = 4;

        // sh_type
        public const uint ShtProgbits = 1;
        public const uint ShtStrTab = 3;

        // sh_flags
        public const ulong ShfWrite = 0x1;
        public const ulong ShfAlloc = 0x2;
        public const ulong ShfExecInstr = 0x4;

        public const ulong PageSize = 0x1000;

        /// <summary>shstrtab 固定前缀："\0.text\0.data\0.shstrtab\0"（.text=1 .data=7 .shstrtab=13）；附加节名在其后。</summary>
        public static readonly byte[] ShStrTabPrefix =
        {
            0, (byte)'.', (byte)'t', (byte)'e', (byte)'x', (byte)'t', 0,
            (byte)'.', (byte)'d', (byte)'a', (byte)'t', (byte)'a', 0,
            (byte)'.', (byte)'s', (byte)'h', (byte)'s', (byte)'t', (byte)'r', (byte)'t', (byte)'a', (byte)'b', 0,
        };
    }

    /// <summary>
    /// ELF64 对象写出器：IObjectWriter 实现——把格式中立 ObjectWriteOptions（.text/.data + 入口 + 基址）
    /// 写为 linux-x64 最小可执行 ET_EXEC（非 PIE 固定基址，绝对寻址零动态链接）。
    /// 布局：ELF 头 + 程序头（PT_LOAD×2 + PT_GNU_STACK）+ .text + .data + 节表 + .shstrtab。
    /// </summary>
    public sealed class ElfObjectWriter : IObjectWriter
    {
        public static readonly ElfObjectWriter Instance = new();

        public byte[] Write(ObjectWriteOptions options)
        {
            if (options.Cpu != Cpu.X64)
            {
                throw new InvalidOperationException("ElfObjectWriter: linux ELF64 目标仅支持 x64。");
            }

            var text = options.Sections.FirstOrDefault(s => s.Name == ".text")
                ?? throw new InvalidOperationException("ElfObjectWriter: 缺少 .text 节。");

            // 可装载节：.text → RX 段；其余（.data/.cocoa…）→ RW 段，连续排列
            var rwSections = options.Sections.Where(s => s.Name != ".text").ToList();
            var rwFirst = rwSections.FirstOrDefault()
                ?? throw new InvalidOperationException("ElfObjectWriter: 缺少可装载 RW 节。");

            var imageBase = options.ImageBase;

            // ---- 文件布局 ----
            var textOffset = Align((uint)(ElfConstants.HeaderSize + 3 * ElfConstants.ProgramHeaderSize), (int)ElfConstants.PageSize);
            var textSize = (uint)text.RawData.Length;
            var rwOffset = Align(textOffset + textSize, (int)ElfConstants.PageSize);

            var rwLayout = new (SectionSpec Section, uint FileOffset)[rwSections.Count];
            var rwPos = rwOffset;
            for (var i = 0; i < rwSections.Count; i++)
            {
                rwPos = Align(rwPos, 8);
                rwLayout[i] = (rwSections[i], rwPos);
                rwPos += (uint)rwSections[i].RawData.Length;
            }

            var rwSize = rwPos - rwOffset;

            // shstrtab：固定前缀（.text=1 .data=7 .shstrtab=13）+ 附加节名
            var shstr = new System.Collections.Generic.List<byte>();
            shstr.AddRange(ElfConstants.ShStrTabPrefix);
            var extraNameOffsets = new System.Collections.Generic.Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var section in options.Sections.Where(s => s.Name != ".text" && s.Name != ".data" && s.Name != ".shstrtab"))
            {
                if (!extraNameOffsets.ContainsKey(section.Name))
                {
                    extraNameOffsets[section.Name] = (uint)shstr.Count;
                    shstr.AddRange(System.Text.Encoding.ASCII.GetBytes(section.Name));
                    shstr.Add(0);
                }
            }

            var shstrBytes = shstr.ToArray();
            var shstrOffset = Align(rwOffset + rwSize, 8);
            var sectionTableOffset = Align((uint)(shstrOffset + (uint)shstrBytes.Length), 8);
            var sectionCount = options.Sections.Count;
            var shnum = sectionCount + 2; // NULL + sections + .shstrtab
            var totalSize = sectionTableOffset + (uint)shnum * ElfConstants.SectionHeaderSize;

            var image = new byte[totalSize];

            var textVaddr = imageBase + text.VirtualAddress;
            var rwVaddr = imageBase + rwFirst.VirtualAddress;
            var entryPoint = imageBase + options.AddressOfEntryPoint;

            // ---- 程序头（PT_LOAD×2 + PT_GNU_STACK）----
            WriteProgramHeader(image, 0x40,
                ElfConstants.PtLoad, ElfConstants.PfR | ElfConstants.PfX,
                textOffset, textVaddr, textSize, ElfConstants.PageSize);
            WriteProgramHeader(image, 0x40 + ElfConstants.ProgramHeaderSize,
                ElfConstants.PtLoad, ElfConstants.PfR | ElfConstants.PfW,
                rwOffset, rwVaddr, rwSize, ElfConstants.PageSize);
            WriteProgramHeader(image, 0x40 + 2 * ElfConstants.ProgramHeaderSize,
                ElfConstants.PtGnuStack, ElfConstants.PfR | ElfConstants.PfW,
                0, 0, 0, 16);

            // ---- ELF 头 ----
            var header = image.AsSpan(0, ElfConstants.HeaderSize);
            header[0x00] = 0x7F; header[0x01] = (byte)'E'; header[0x02] = (byte)'L'; header[0x03] = (byte)'F';
            header[0x04] = ElfConstants.Elf64Class;
            header[0x05] = ElfConstants.LittleEndian;
            header[0x06] = 1; // EV_CURRENT
            header[0x07] = ElfConstants.SysVAbi;
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x10), ElfConstants.EtExec);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x12), ElfConstants.EmX86_64);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0x14), 1); // e_version
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(0x18), entryPoint);
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(0x20), 0x40); // e_phoff
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(0x28), sectionTableOffset); // e_shoff
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0x30), 0); // e_flags
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x34), ElfConstants.HeaderSize);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x36), ElfConstants.ProgramHeaderSize);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x38), 3); // e_phnum
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x3A), ElfConstants.SectionHeaderSize);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x3C), (ushort)shnum);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x3E), (ushort)(shnum - 1)); // e_shstrndx

            // ---- 节数据 ----
            text.RawData.CopyTo(image, (int)textOffset);
            foreach (var (section, fileOffset) in rwLayout)
            {
                section.RawData.CopyTo(image, (int)fileOffset);
            }

            shstrBytes.CopyTo(image, (int)shstrOffset);

            // ---- 节表：NULL + 逐节 + .shstrtab ----
            WriteSectionHeader(image, (int)sectionTableOffset, 0, 0, 0, 0, 0, 0, 0);
            var sectionIndex = 1;
            WriteSectionHeader(image, (int)(sectionTableOffset + sectionIndex * ElfConstants.SectionHeaderSize),
                1, ElfConstants.ShtProgbits, ElfConstants.ShfAlloc | ElfConstants.ShfExecInstr,
                textVaddr, textOffset, textSize, 16);
            sectionIndex++;

            foreach (var (section, fileOffset) in rwLayout)
            {
                var addr = rwVaddr + (fileOffset - rwOffset);
                var nameOffset = section.Name switch
                {
                    ".data" => 7u,
                    _ => extraNameOffsets[section.Name],
                };
                WriteSectionHeader(image, (int)(sectionTableOffset + sectionIndex * ElfConstants.SectionHeaderSize),
                    nameOffset, ElfConstants.ShtProgbits, ElfConstants.ShfAlloc | ElfConstants.ShfWrite,
                    addr, fileOffset, (uint)section.RawData.Length, 8);
                sectionIndex++;
            }

            WriteSectionHeader(image, (int)(sectionTableOffset + sectionIndex * ElfConstants.SectionHeaderSize),
                13, ElfConstants.ShtStrTab, 0, 0, shstrOffset, (uint)shstrBytes.Length, 1);

            return image;
        }

        private static void WriteProgramHeader(byte[] image, int offset, uint type, uint flags, ulong fileOffset, ulong vaddr, uint size, ulong alignment)
        {
            var s = image.AsSpan(offset, ElfConstants.ProgramHeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0x00), type);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0x04), flags);
            BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0x08), fileOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0x10), vaddr);
            BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0x18), vaddr); // p_paddr = p_vaddr（ET_EXEC）
            BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0x20), size);
            BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0x28), size);
            BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0x30), alignment);
        }

        private static void WriteSectionHeader(byte[] image, int offset, uint name, uint type, ulong flags, ulong addr, ulong fileOffset, uint size, ulong alignment)
        {
            var s = image.AsSpan(offset, ElfConstants.SectionHeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0x00), name);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0x04), type);
            BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0x08), flags);
            BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0x10), addr);
            BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0x18), fileOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0x20), size);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0x28), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0x2C), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0x30), alignment);
            BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(0x38), 0);
        }

        private static uint Align(uint value, int alignment)
        {
            var rem = value % alignment;
            return rem == 0 ? value : value + (uint)(alignment - (int)rem);
        }
    }
}