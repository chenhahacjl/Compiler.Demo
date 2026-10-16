using Cocoa.CodeGen.Elf;
using Cocoa.Targeting;
using System;
using System.Buffers.Binary;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeGen.Elf
{
    /// <summary>
    /// T3.3：ElfObjectWriter 结构断言集——ELF64 ET_EXEC（非 PIE 固定基址）：
    /// 魔数/类型/机器/入口 + 程序头（PT_LOAD×2 + PT_GNU_STACK）offset↔vaddr 一致 + 节表回查。
    /// </summary>
    public class ElfObjectWriterTests
    {
        private static ObjectWriteOptions Build()
        {
            var options = new ObjectWriteOptions(
                TargetOS.Linux, Cpu.X64, 0x400000UL, 0, 0x1048, 0x1000, 0x1000);
            options.Sections.Add(new SectionSpec(".text", new byte[0x200], 0x1000, 0));
            options.Sections.Add(new SectionSpec(".data", new byte[] { 1, 2, 3, 4 }, 0x2000, 0));
            return options;
        }

        [Fact]
        public void Header_IsValidElf64Exec_X86_64()
        {
            var image = ElfObjectWriter.Instance.Write(Build());

            Assert.Equal(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }, image.Take(4).ToArray());
            Assert.Equal(2, image[4]);   // ELFCLASS64
            Assert.Equal(1, image[5]);   // LSB
            Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x10)));  // ET_EXEC
            Assert.Equal(0x3E, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x12))); // EM_X86_64
        }

        [Fact]
        public void Entry_Points_Into_Text_Segment()
        {
            var image = ElfObjectWriter.Instance.Write(Build());

            var entry = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x18));
            // entry = textVaddr + 0x48（TextRva 0x1000 + AddressOfEntryPoint 0x48 的相对偏移）
            Assert.Equal(0x400000UL + 0x1048, entry);

            var ph0Vaddr = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x40 + 0x10));
            var ph0Size = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x40 + 0x28));
            Assert.InRange(entry, ph0Vaddr, ph0Vaddr + ph0Size);
        }

        [Fact]
        public void ProgramHeaders_Load_At_Fixed_Base()
        {
            var image = ElfObjectWriter.Instance.Write(Build());
            var phnum = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x38));
            Assert.Equal(3, phnum);

            // PT_LOAD #0：RX，偏移 0x1000，vaddr 0x401000
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x40)));
            Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x44)));
            Assert.Equal(0x1000u, BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x48)));
            Assert.Equal(0x401000UL, BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x50)));
            // p_offset ≡ p_vaddr (mod 0x1000)
            Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x48)) % 0x1000UL);
            Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x50)) % 0x1000UL);

            // PT_LOAD #1：RW，.data
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x40 + 56)));
            Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x40 + 56 + 4)));
            Assert.Equal(0x402000UL, BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x40 + 56 + 0x10)));

            // PT_GNU_STACK：RW、无内容
            Assert.Equal(0x6474E551u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x40 + 112)));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x40 + 112 + 0x20)));
        }

        [Fact]
        public void SectionHeaders_Map_To_Segments()
        {
            var image = ElfObjectWriter.Instance.Write(Build());
            var shoff = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x28));
            var shnum = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x3C));
            Assert.Equal(4, shnum); // NULL + .text + .data + .shstrtab

            // .text（索引 1）：PROGBITS、ALLOC|EXEC、vaddr=0x401000、offset=0x1000
            var textOff = shoff + 1 * 64;
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)textOff + 0x00)));   // sh_name=1
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)textOff + 0x04)));   // PROGBITS
            Assert.Equal(6UL, BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan((int)textOff + 0x08)));  // ALLOC|EXECINSTR
            Assert.Equal(0x401000UL, BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan((int)textOff + 0x10)));
            Assert.Equal(0x1000u, BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan((int)textOff + 0x18)));
            Assert.Equal(0x200u, BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan((int)textOff + 0x20)));

            // .data（索引 2）：ALLOC|WRITE、vaddr=0x402000、内容可读回
            var dataOff = shoff + 2 * 64;
            Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)dataOff + 0x00)));   // sh_name=7
            Assert.Equal(3UL, BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan((int)dataOff + 0x08)));
            Assert.Equal(0x402000UL, BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan((int)dataOff + 0x10)));

            var dataRawOffset = (int)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan((int)dataOff + 0x18));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, image.Skip(dataRawOffset).Take(4).ToArray());

            // .shstrtab（索引 3）：STRTAB
            Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)(shoff + 3 * 64) + 0x04)));
        }

        [Fact]
        public void SectionName_Strings_Lookup()
        {
            var image = ElfObjectWriter.Instance.Write(Build());
            var shoff = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x28));
            var shstr = (int)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan((int)(shoff + 3 * 64) + 0x18));

            // "\0.text\0.data\0.shstrtab\0"：索引 1=.text，7=.data，13=.shstrtab
            Assert.Equal(".text", AsCString(image, shstr + 1));
            Assert.Equal(".data", AsCString(image, shstr + 7));
            Assert.Equal(".shstrtab", AsCString(image, shstr + 13));
        }

        [Fact]
        public void CocoaMetadata_ExtraSection_Embedded()
        {
            // 附 .cocoa 节：shnum=5、节名经 shstrtab 附加偏移解析、文件数据可回读。
            var options = Build();
            options.Sections.Add(new SectionSpec(".cocoa", new byte[] { 0x43, 0x4F, 0x43, 0x4F, 0x41, 0 }, 0x2110, 0));
            var image = ElfObjectWriter.Instance.Write(options);

            var shoff = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x28));
            var shnum = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x3C));
            Assert.Equal(5, shnum);

            // .cocoa 段头（索引 3）：name=23（附加名区，前缀 "\0.text\0.data\0.shstrtab\0" 后）、PROGBITS、RW
            var cocoaOff = shoff + 3 * 64;
            Assert.Equal(23u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)cocoaOff + 0x00)));
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)cocoaOff + 0x04)));
            Assert.Equal(3UL, BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan((int)cocoaOff + 0x08)));

            var dataRaw = (int)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan((int)cocoaOff + 0x18));
            Assert.Equal(new byte[] { 0x43, 0x4F, 0x43, 0x4F, 0x41, 0 }, image.Skip(dataRaw).Take(6).ToArray());
        }

        private static string AsCString(byte[] image, int offset)
        {
            var sb = new System.Text.StringBuilder();
            while (offset < image.Length && image[offset] != 0)
            {
                sb.Append((char)image[offset++]);
            }
            return sb.ToString();
        }
    }
}