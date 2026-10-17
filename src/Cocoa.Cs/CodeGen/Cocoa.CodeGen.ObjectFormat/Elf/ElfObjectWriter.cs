using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cocoa.Targeting;

namespace Cocoa.CodeGen.ObjectFormat.Elf
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
        public const uint PtDynamic = 2;
        public const uint PtInterp = 3;
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
            if (options.Arch != Architecture.X64)
            {
                throw new InvalidOperationException("ElfObjectWriter: linux ELF64 目标仅支持 x64。");
            }

            // N3：动态导入 → 动态链接路径（与零导入静态交付字节无关）
            if (options.Imports is { Count: > 0 })
            {
                return WriteWithDynamicImports(options);
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

        /// <summary>
        /// N3：动态链接路径——PT_INTERP/PT_DYNAMIC + .dynsym/.dynstr/.hash/.rela.dyn，
        /// GLOB_DAT 重定位把每个导入符号地址写入 data 段 IAT 槽（加载器填充，等同 Windows IAT 语义）。
        /// 布局：ELF 头 + 程序头（PT_LOAD×2 + PT_INTERP + PT_DYNAMIC + PT_GNU_STACK）+
        /// .text + RW(.data [.cocoa] + .interp/.dynstr/.dynsym/.hash/.dynamic/.rela.dyn) + 节表 + .shstrtab。
        /// </summary>
        private byte[] WriteWithDynamicImports(ObjectWriteOptions options)
        {
            var imageBase = options.ImageBase;
            var imports = options.Imports!;
            var text = options.Sections.First(s => s.Name == ".text");
            var rwSections = options.Sections.Where(s => s.Name != ".text").ToList();
            var rwFirst = rwSections[0];

            var libs = new List<string>();
            foreach (var import in imports)
            {
                if (!libs.Contains(import.Library))
                {
                    libs.Add(import.Library);
                }
            }

            // ---- .dynstr：空名 + 库名 + 符号名（仅存偏移；DT_NEEDED 引用 = dynstr 偏移非 vaddr）----
            var dynstr = new List<byte> { 0 };
            var libOffsets = new Dictionary<string, uint>();
            foreach (var lib in libs)
            {
                libOffsets[lib] = (uint)dynstr.Count;
                dynstr.AddRange(Encoding.ASCII.GetBytes(lib));
                dynstr.Add(0);
            }

            var symOffsets = new uint[imports.Count];
            for (var i = 0; i < imports.Count; i++)
            {
                symOffsets[i] = (uint)dynstr.Count;
                dynstr.AddRange(Encoding.ASCII.GetBytes(imports[i].Symbol));
                dynstr.Add(0);
            }

            var symCount = imports.Count + 1;
            var dynstrBytes = dynstr.ToArray();

            // ---- SysV .hash（nbucket=1，chain 回溯链）----
            var bucket = new uint[1];
            var hashTable = new uint[2 + 1 + symCount];
            hashTable[0] = 1; // nbucket
            hashTable[1] = (uint)symCount; // nchain
            for (var i = 0; i < imports.Count; i++)
            {
                var idx = (uint)(i + 1);
                var h = ElfHash(imports[i].Symbol);
                _ = h;
                hashTable[2] = idx; // bucket[0]
                hashTable[2 + 1 + idx] = bucket[0];
                bucket[0] = idx;
            }

            // ---- 布局 ----
            const int phCount = 5;
            var textOffset = Align((uint)(ElfConstants.HeaderSize + phCount * ElfConstants.ProgramHeaderSize), (int)ElfConstants.PageSize);
            var textSize = (uint)text.RawData.Length;
            var rwOffset = Align(textOffset + textSize, (int)ElfConstants.PageSize);
            var rwVaddr = imageBase + rwFirst.VirtualAddress;

            var rwLayout = new List<(string Name, ulong Vaddr, uint FileOffset, uint Size, uint Align, ulong ShFlags, uint ShType)>();
            var rwPos = rwOffset;
            foreach (var section in rwSections)
            {
                rwPos = Align(rwPos, 8);
                rwLayout.Add((section.Name, rwVaddr + (rwPos - rwOffset), rwPos, (uint)section.RawData.Length, 8,
                    ElfConstants.ShfAlloc | ElfConstants.ShfWrite, ElfConstants.ShtProgbits));
                rwPos += (uint)section.RawData.Length;
            }

            var interpBytes = Encoding.ASCII.GetBytes("/lib64/ld-linux-x86-64.so.2\0");
            rwPos = Align(rwPos, 8);
            var interpSeg = rwPos;
            var dynStrSeg = Aligninterp(interpSeg + (uint)interpBytes.Length, 8);
            var dynSymSeg = Aligninterp(dynStrSeg + (uint)dynstrBytes.Length, 8);
            var dynSymSize = (uint)(symCount * 24);
            var hashSeg = Aligninterp(dynSymSeg + dynSymSize, 4);
            var hashSize = (uint)(hashTable.Length * 4);
            var dynamicSize = (uint)((libs.Count + 5 + (imports.Count > 0 ? 3 : 0) + 1) * 16);
            var dynamicSeg = Aligninterp(hashSeg + hashSize, 8);
            var relaSeg = Aligninterp(dynamicSeg + dynamicSize, 8);
            var relaEntries = new (ulong Vaddr, ulong SymIndex)[imports.Count];
            for (var i = 0; i < imports.Count; i++)
            {
                relaEntries[i] = (rwVaddr + (ulong)imports[i].SlotDataOffset, (ulong)(i + 1));
            }

            var relaSize = (uint)(relaEntries.Length * 24);
            var rwEnd = relaSeg + relaSize;
            var rwSize = rwEnd - rwOffset;
            var shstrOffset = Align(rwEnd, 8);

            // ---- 节表描述 ----
            var allSections = new List<(string Name, ulong Vaddr, uint FileOffset, uint Size, uint Align, ulong ShFlags, uint ShType)>();
            allSections.Add((".text", imageBase + text.VirtualAddress, textOffset, textSize, 16,
                ElfConstants.ShfAlloc | ElfConstants.ShfExecInstr, ElfConstants.ShtProgbits));
            allSections.AddRange(rwLayout);
            allSections.Add((".interp", rwVaddr + (interpSeg - rwOffset), interpSeg, (uint)interpBytes.Length, 1,
                ElfConstants.ShfAlloc, ElfConstants.ShtProgbits));
            allSections.Add((".dynstr", rwVaddr + (dynStrSeg - rwOffset), dynStrSeg, (uint)dynstrBytes.Length, 1,
                ElfConstants.ShfAlloc, ElfConstants.ShtStrTab));
            allSections.Add((".dynsym", rwVaddr + (dynSymSeg - rwOffset), dynSymSeg, dynSymSize, 8,
                ElfConstants.ShfAlloc, ElfConstants.ShtProgbits));
            allSections.Add((".hash", rwVaddr + (hashSeg - rwOffset), hashSeg, hashSize, 4,
                ElfConstants.ShfAlloc, ElfConstants.ShtProgbits));
            allSections.Add((".dynamic", rwVaddr + (dynamicSeg - rwOffset), dynamicSeg, dynamicSize, 8,
                ElfConstants.ShfAlloc | ElfConstants.ShfWrite, ElfConstants.ShtProgbits));
            allSections.Add((".rela.dyn", rwVaddr + (relaSeg - rwOffset), relaSeg, relaSize, 8,
                ElfConstants.ShfAlloc, ElfConstants.ShtProgbits));

            var dynamicBytes = BuildDynamic(libs, libOffsets,
                dynstrBytes.Length,
                rwVaddr + (dynStrSeg - rwOffset), rwVaddr + (dynSymSeg - rwOffset), rwVaddr + (hashSeg - rwOffset),
                rwVaddr + (relaSeg - rwOffset), relaSize, relaEntries.Length);

            // ---- shstrtab（固定前缀 + 附加节名）----
            var shstr = new List<byte>();
            shstr.AddRange(ElfConstants.ShStrTabPrefix);
            var nameOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var name in new[] { ".cocoa", ".interp", ".dynstr", ".dynsym", ".hash", ".dynamic", ".rela.dyn" })
            {
                if (nameOffsets.ContainsKey(name))
                {
                    continue;
                }

                nameOffsets[name] = (uint)shstr.Count;
                shstr.AddRange(Encoding.ASCII.GetBytes(name));
                shstr.Add(0);
            }

            var shstrBytes = shstr.ToArray();
            var sectionTableOffset = Align(shstrOffset + (uint)shstrBytes.Length, 8);
            var shnum = allSections.Count + 2;
            var totalSize = sectionTableOffset + (uint)shnum * ElfConstants.SectionHeaderSize;
            var image = new byte[totalSize];

            // ---- 程序头（PT_LOAD#0 从文件 0/基址起覆盖 ELF 头+phdr 页——动态加载器须映射读 phdr）----
            WriteProgramHeader(image, 0x40, ElfConstants.PtLoad, ElfConstants.PfR | ElfConstants.PfX, 0, imageBase, textOffset + textSize, ElfConstants.PageSize);
            WriteProgramHeader(image, 0x40 + 56, ElfConstants.PtLoad, ElfConstants.PfR | ElfConstants.PfW, rwOffset, rwVaddr, rwSize, ElfConstants.PageSize);
            WriteProgramHeader(image, 0x40 + 112, ElfConstants.PtInterp, ElfConstants.PfR, interpSeg, rwVaddr + (interpSeg - rwOffset), (uint)interpBytes.Length, 1);
            WriteProgramHeader(image, 0x40 + 168, ElfConstants.PtDynamic, ElfConstants.PfR | ElfConstants.PfW, dynamicSeg, rwVaddr + (dynamicSeg - rwOffset), (uint)dynamicBytes.Length, 8);
            WriteProgramHeader(image, 0x40 + 224, ElfConstants.PtGnuStack, ElfConstants.PfR | ElfConstants.PfW, 0, 0, 0, 16);

            // ---- ELF 头 ----
            var header = image.AsSpan(0, ElfConstants.HeaderSize);
            header[0x00] = 0x7F; header[0x01] = (byte)'E'; header[0x02] = (byte)'L'; header[0x03] = (byte)'F';
            header[0x04] = ElfConstants.Elf64Class;
            header[0x05] = ElfConstants.LittleEndian;
            header[0x06] = 1;
            header[0x07] = ElfConstants.SysVAbi;
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x10), ElfConstants.EtExec);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x12), ElfConstants.EmX86_64);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0x14), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(0x18), imageBase + options.AddressOfEntryPoint);
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(0x20), 0x40);
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(0x28), sectionTableOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0x30), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x34), ElfConstants.HeaderSize);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x36), ElfConstants.ProgramHeaderSize);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x38), phCount);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x3A), ElfConstants.SectionHeaderSize);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x3C), (ushort)shnum);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0x3E), (ushort)(shnum - 1));

            // ---- 节数据 ----
            text.RawData.CopyTo(image, (int)textOffset);
            foreach (var section in rwLayout)
            {
                options.Sections.First(s => s.Name == section.Name).RawData.CopyTo(image, (int)section.FileOffset);
            }

            interpBytes.CopyTo(image, (int)interpSeg);
            dynstrBytes.CopyTo(image, (int)dynStrSeg);
            WriteDynSym(image, (int)dynSymSeg, imports, symOffsets);
            WriteHash(image, (int)hashSeg, hashTable);
            dynamicBytes.CopyTo(image, (int)dynamicSeg);
            WriteRela(image, (int)relaSeg, relaEntries);
            shstrBytes.CopyTo(image, (int)shstrOffset);

            // ---- 节表 ----
            WriteSectionHeader(image, (int)sectionTableOffset, 0, 0, 0, 0, 0, 0, 0);
            var index = 1;
            foreach (var section in allSections)
            {
                var siName = section.Name == ".text" ? 1u : section.Name == ".data" ? 7u : nameOffsets[section.Name];
                WriteSectionHeader(image, (int)(sectionTableOffset + (ulong)index * ElfConstants.SectionHeaderSize),
                    siName, section.ShType, section.ShFlags, section.Vaddr, section.FileOffset, section.Size, section.Align);
                index++;
            }

            WriteSectionHeader(image, (int)(sectionTableOffset + (ulong)index * ElfConstants.SectionHeaderSize),
                13, ElfConstants.ShtStrTab, 0, 0, shstrOffset, (uint)shstrBytes.Length, 1);

            return image;
        }

        private static uint Aligninterp(uint value, uint alignment)
        {
            var rem = value % alignment;
            return rem == 0 ? value : value + (alignment - rem);
        }

        private static uint ElfHash(string name)
        {
            uint h = 0;
            foreach (var c in name)
            {
                h = (h << 4) + (uint)c;
                var g = h & 0xF0000000u;
                if (g != 0)
                {
                    h ^= g >> 24;
                }

                h &= ~g;
            }

            return h;
        }

        private static byte[] BuildDynamic(
            IReadOnlyList<string> libs,
            Dictionary<string, uint> libOffsets,
            int dynstrSize,
            ulong dynstrVaddr,
            ulong dynsymVaddr,
            ulong hashVaddr,
            ulong relaVaddr,
            uint relaSize,
            int relaCount)
        {
            var b = new List<byte>();
            void PutDyn(long tag, ulong value)
            {
                b.Add((byte)tag); b.Add((byte)(tag >> 8)); b.Add((byte)(tag >> 16)); b.Add((byte)(tag >> 24));
                b.Add((byte)(tag >> 32)); b.Add((byte)(tag >> 40)); b.Add((byte)(tag >> 48)); b.Add((byte)(tag >> 56));
                b.Add((byte)value); b.Add((byte)(value >> 8)); b.Add((byte)(value >> 16)); b.Add((byte)(value >> 24));
                b.Add((byte)(value >> 32)); b.Add((byte)(value >> 40)); b.Add((byte)(value >> 48)); b.Add((byte)(value >> 56));
            }

            // DT_NEEDED 的 d_val = dynstr 偏移（相对 DT_STRTAB），非绝对 vaddr！
            foreach (var lib in libs)
            {
                PutDyn(DtNeeded, libOffsets[lib]);
            }

            PutDyn(DtHash, hashVaddr);
            PutDyn(DtStrTab, dynstrVaddr);
            PutDyn(DtSymTab, dynsymVaddr);
            PutDyn(DtStrsz, (ulong)dynstrSize);
            PutDyn(DtSymEnt, 24);
            if (relaCount > 0)
            {
                // 不发 DT_RELACOUNT！glibc 会把 .rela.dyn 前 RELACOUNT 条当 RELATIVE（l_addr+addend）
                // 处理——本表全为 GLOB_DAT，误设会写 0（此前跳 0x0 崩溃的根因）。
                PutDyn(DtRela, relaVaddr);
                PutDyn(DtRelasz, relaSize);
                PutDyn(DtRelaent, 24);
            }

            PutDyn(DtNull, 0);
            return b.ToArray();
        }

        private static void WriteDynSym(byte[] image, int offset, IReadOnlyList<DynamicImport> imports, uint[] symOffsets)
        {
            for (var i = 0; i < 24; i++)
            {
                image[offset + i] = 0; // 符号 0：全零
            }

            for (var i = 0; i < imports.Count; i++)
            {
                var o = offset + (i + 1) * 24;
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(o + 0x00), symOffsets[i]);
                image[o + 0x04] = 0x12; // STB_GLOBAL | STT_FUNC
                image[o + 0x05] = 0;
                BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(o + 0x06), 0); // SHN_UNDEF
                BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(o + 0x08), 0);
                BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(o + 0x10), 0);
            }
        }

        private static void WriteHash(byte[] image, int offset, uint[] table)
        {
            for (var i = 0; i < table.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + i * 4), table[i]);
            }
        }

        private static void WriteRela(byte[] image, int offset, IReadOnlyList<(ulong Vaddr, ulong SymIndex)> entries)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var o = offset + i * 24;
                BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(o + 0x00), entries[i].Vaddr);
                BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(o + 0x08), (entries[i].SymIndex << 32) | R_X86_64_GLOB_DAT);
                BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(o + 0x10), 0);
            }
        }

        private const long DtNull = 0;
        private const long DtNeeded = 1;
        private const long DtHash = 4;
        private const long DtStrTab = 5;
        private const long DtSymTab = 6;
        private const long DtRela = 7;
        private const long DtRelasz = 8;
        private const long DtRelaent = 9;
        private const long DtStrsz = 10;
        private const long DtSymEnt = 11;
        private const long DtRelacount = 0x6FFFFFF9;
        private const ulong R_X86_64_GLOB_DAT = 6;

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
