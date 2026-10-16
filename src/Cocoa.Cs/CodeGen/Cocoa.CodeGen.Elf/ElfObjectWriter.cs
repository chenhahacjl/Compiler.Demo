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
            var data = options.Sections.FirstOrDefault(s => s.Name == ".data")
                ?? throw new InvalidOperationException("ElfObjectWriter: 缺少 .data 节。");

            var imageBase = options.ImageBase;

            // ---- 文件布局 ----
            var textOffset = Align((uint)(ElfConstants.HeaderSize + 3 * ElfConstants.ProgramHeaderSize), (int)ElfConstants.PageSize);
            var dataOffset = Align(textOffset + (uint)text.RawData.Length, (int)ElfConstants.PageSize);
            var shstrOffset = Align(dataOffset + (uint)data.RawData.Length, 8);
            var sectionTableOffset = Align((uint)(shstrOffset + (uint)ShStrTabBytes.Length), 8);
            var totalSize = sectionTableOffset + 4 * ElfConstants.SectionHeaderSize;

            var image = new byte[totalSize];

            var textVaddr = imageBase + text.VirtualAddress;
            var dataVaddr = imageBase + data.VirtualAddress;
            var entryPoint = imageBase + options.AddressOfEntryPoint;

            // ---- 程序头（PT_LOAD×2 + PT_GNU_STACK）----
            WriteProgramHeader(image, 0x40,
                ElfConstants.PtLoad, ElfConstants.PfR | ElfConstants.PfX,
                textOffset, textVaddr, (uint)text.RawData.Length, ElfConstants.PageSize);
            WriteProgramHeader(image, 0x40 + ElfConstants.ProgramHeaderSize,
                ElfConstants.PtLoad, ElfConstants.PfR | ElfConstants.PfW,
                dataOffset, dataVaddr, (uint)data.RawData.Length, ElfConstants.PageSize);
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
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x3C), 4); // e_shnum
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x3E), 3); // e_shstrndx

            // ---- 节数据 ----
            text.RawData.CopyTo(image, (int)textOffset);
            data.RawData.CopyTo(image, (int)dataOffset);
            ShStrTabBytes.CopyTo(image, (int)shstrOffset);

            // ---- 节表 ----
            WriteSectionHeader(image, (int)(sectionTableOffset + 0 * ElfConstants.SectionHeaderSize), 0, 0, 0, 0, 0, 0, 0);
            WriteSectionHeader(image, (int)(sectionTableOffset + 1 * ElfConstants.SectionHeaderSize),
                1, ElfConstants.ShtProgbits, ElfConstants.ShfAlloc | ElfConstants.ShfExecInstr,
                textVaddr, textOffset, (uint)text.RawData.Length, 16);
            WriteSectionHeader(image, (int)(sectionTableOffset + 2 * ElfConstants.SectionHeaderSize),
                7, ElfConstants.ShtProgbits, ElfConstants.ShfAlloc | ElfConstants.ShfWrite,
                dataVaddr, dataOffset, (uint)data.RawData.Length, 8);
            WriteSectionHeader(image, (int)(sectionTableOffset + 3 * ElfConstants.SectionHeaderSize),
                13, ElfConstants.ShtStrTab, 0, 0, shstrOffset, (uint)ShStrTabBytes.Length, 1);

            return image;
        }

        /// <summary>shstrtab 内容："\0.text\0.data\0.shstrtab\0"（偏移 .text=1 .data=7 .shstrtab=13）。</summary>
        private static readonly byte[] ShStrTabBytes =
        {
            0, (byte)'.', (byte)'t', (byte)'e', (byte)'x', (byte)'t', 0,
            (byte)'.', (byte)'d', (byte)'a', (byte)'t', (byte)'a', 0,
            (byte)'.', (byte)'s', (byte)'h', (byte)'s', (byte)'t', (byte)'r', (byte)'t', (byte)'a', (byte)'b', 0,
        };

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