using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeGen.Native;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.CodeGen.Elf
{
    /// <summary>
    /// S4（native-s1）：Linux 共享库 .so 发射验收——EmitNativeLibrary（linux-x64）产出 ET_DYN、entry=0、
    /// 无 PT_INTERP、.dynsym 导出 STB_GLOBAL 定义（st_shndx=.text、st_value=镜像内偏移）。
    /// 字节级断言（Windows 环境无法执行 ELF）。
    /// </summary>
    public class ElfSharedLibraryTests
    {
        private static readonly TargetPlatform Platform = new(TargetOS.Linux, Architecture.X64);

        [Fact]
        public void Emit_Produces_EtDyn_SharedObject()
        {
            var image = EmitFixture();
            Assert.Equal(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }, image.Take(4).ToArray());
            Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x10)));  // ET_DYN
            Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x18))); // e_entry = 0
            Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x38)));   // e_phnum（无 PT_INTERP）
        }

        [Fact]
        public void Emit_DynSym_Defines_Exports()
        {
            var image = EmitFixture();
            var syms = ReadDynSyms(image);
            Assert.True(syms.Count > 0, "expected exported symbols");

            // 库函数与运行时原语都应在 .dynsym 定义为 STB_GLOBAL 且指向 .text（st_shndx=1, st_value≠0）
            var defined = syms.Where(s => s.Shndx == 1 && s.Value != 0).ToArray();
            Assert.True(defined.Length > 0, "expected defined (export) symbols in .dynsym");
            Assert.Contains(defined, s => s.Name.StartsWith("Cocoa_Lib_", StringComparison.Ordinal));
            Assert.Contains(defined, s => s.Name.StartsWith("Cocoa_Rt_", StringComparison.Ordinal));

            // 无导入（Linux 运行时裸 syscall，零外部依赖）→ 不要求 UNDEF；有则也合法
        }

        [Fact]
        public void Emit_DynSym_Values_Are_ImageOffsets()
        {
            var image = EmitFixture();
            var syms = ReadDynSyms(image);
            var export = syms.First(s => s.Shndx == 1 && s.Value != 0 && s.Name.StartsWith("Cocoa_Lib_", StringComparison.Ordinal));
            // st_value = 镜像内偏移（.text 基址 0x1000 起）
            Assert.True(export.Value >= 0x1000, $"export value {export.Value} should be within .text");
            Assert.True(export.Value < (ulong)(0x1000 + image.Length), "export value out of image range");
        }

        private static byte[] EmitFixture()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-elf-so-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var coaPath = Path.Combine(dir, "Lib.coa");
            var soPath = Path.Combine(dir, "Lib.Native.so");

            var diags = Compilation.Create(SyntaxTree.Parse(@"namespace Lib {
    public function Twice(a: i32): i32
    {
        return a * 2
    }
}
")).EmitCoa("Lib", coaPath);
            Assert.True(!diags.HasErrors(), "coa diags: " + string.Join(" | ", diags.Select(d => d.Message)));

            EmitNativeLibrary.Emit(CoaSerializer.Load(coaPath, SystemLibrary.Load()), soPath, Platform);
            return File.ReadAllBytes(soPath);
        }

        private readonly record struct DynSym(string Name, byte Info, ushort Shndx, ulong Value);

        private static List<DynSym> ReadDynSyms(byte[] image)
        {
            var eShoff = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x28));
            var eShnum = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x3C));
            var eShstrndx = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x3E));

            // 读取 shstrtab
            var shstrHdr = (int)eShoff + eShstrndx * 64;
            var shstrOff = (int)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(shstrHdr + 0x18));
            var shstrSize = (int)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(shstrHdr + 0x20));
            string SectionName(int nameOff) => ReadCStr(image, shstrOff + nameOff, shstrSize);

            uint dynSymOff = 0, dynSymSize = 0;
            uint dynStrOff = 0, dynStrSize = 0;
            for (var i = 1; i < eShnum; i++)
            {
                var hdr = (int)eShoff + i * 64;
                var name = SectionName((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(hdr + 0)));
                var type = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(hdr + 4));
                var off = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(hdr + 0x18));
                var size = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(hdr + 0x20));
                if (name == ".dynsym" && type == 11) { dynSymOff = (uint)off; dynSymSize = (uint)size; }
                if (name == ".dynstr" && type == 3) { dynStrOff = (uint)off; dynStrSize = (uint)size; }
            }

            Assert.True(dynSymOff != 0 && dynStrOff != 0, "missing .dynsym/.dynstr");
            var result = new List<DynSym>();
            for (var o = 0UL; o < dynSymSize; o += 24)
            {
                var stName = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)(dynSymOff + o)));
                var stInfo = image[(int)(dynSymOff + o + 4)];
                var stShndx = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan((int)(dynSymOff + o + 6)));
                var stValue = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan((int)(dynSymOff + o + 8)));
                var name = stName == 0 ? "" : ReadCStr(image, (int)(dynStrOff + stName), (int)dynStrSize);
                result.Add(new DynSym(name, stInfo, stShndx, stValue));
            }

            return result;
        }

        private static string ReadCStr(byte[] image, int offset, int limit)
        {
            var end = offset;
            while (end < offset + limit && end < image.Length && image[end] != 0)
            {
                end++;
            }

            return System.Text.Encoding.ASCII.GetString(image, offset, end - offset);
        }
    }
}