using Cocoa.CodeGen.ObjectFormat.PE;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeGen.PE
{
    /// <summary>COFF 符号表构建器回读验证：主符号数/函数名/RVA/文件符号/.text 行号 aux。</summary>
    public class CoffSymbolTableBuilderTests
    {
        [Fact]
        public void Build_Emits_FunctionSymbols_Rvas_LineRecords()
        {
            var functions = new[]
            {
                new CoffSymbolTableBuilder.CoffFunctionSymbol("Main", 0x1000, "main.co", 5),
                new CoffSymbolTableBuilder.CoffFunctionSymbol("Add", 0x1080, "main.co", 12),
                new CoffSymbolTableBuilder.CoffFunctionSymbol("Helper", 0x1200, "lib.co", 3),
            };

            var (blob, count) = CoffSymbolTableBuilder.Build(functions, 0x1500);

            // 符号表总记录数 = 文件符号(2 主 + 2 aux) + 函数(3) + .text(1 主 + 1 节定义 aux + 1 行号 aux) = 10
            Assert.Equal(10, count);
            Assert.True(blob.Length > count * 18, "blob 应含符号记录 + 字符串表");

            var symbols = Parse(blob, count);
            var names = symbols.Select(s => s.Name).ToArray();
            Assert.Contains(".text", names);
            Assert.Contains("Main", names);
            Assert.Contains("Add", names);
            Assert.Contains("Helper", names);
            Assert.Contains("main.co", names);
            Assert.Contains("lib.co", names);

            // 函数符号：节 1（.text）、Value=RVA、StorageClass=External
            var main = symbols.Single(s => s.Name == "Main");
            Assert.Equal(1, main.Section);
            Assert.Equal(0x1000u, main.Value);
            Assert.Equal(0x02, main.StorageClass);
            Assert.Equal(0x0020, main.Type);

            // .text 节符号：STATIC，auxCount = 1 节定义 + ceil(3/3)=1 行号块 = 2
            var text = symbols.Single(s => s.Name == ".text");
            Assert.Equal(0x03, text.StorageClass);
            Assert.Equal(2, text.AuxCount);
            Assert.Equal(0u, text.Value);

            // 行号 aux：3 条 IMAGE_LINENUMBER（RVA, 行号）——紧随节定义 aux 之后
            var lineAuxOffset = text.RecordOffset + 18 + 18;
            var lineCount = (text.AuxCount - 1) * 3;
            var lines = new List<(uint Rva, ushort Line)>();
            for (var i = 0; i < lineCount; i++)
            {
                var offset = lineAuxOffset + i * 6;
                lines.Add((BitConverter.ToUInt32(blob, offset), BitConverter.ToUInt16(blob, offset + 4)));
            }

            Assert.Contains((0x1000u, (ushort)5), lines);
            Assert.Contains((0x1080u, (ushort)12), lines);
            Assert.Contains((0x1200u, (ushort)3), lines);

            // 节定义 aux（节符号后第一个 aux）：Length = 代码长度
            var sectionDefOffset = text.RecordOffset + 18;
            Assert.Equal(0x1500u, BitConverter.ToUInt32(blob, sectionDefOffset));
        }

        [Fact]
        public void Build_Empty_ReturnsEmpty()
        {
            var (blob, count) = CoffSymbolTableBuilder.Build(Array.Empty<CoffSymbolTableBuilder.CoffFunctionSymbol>(), 0x100);
            Assert.Empty(blob);
            Assert.Equal(0, count);
        }

        private sealed record Parsed(int RecordOffset, string Name, uint Value, int Section, ushort Type, byte StorageClass, byte AuxCount);

        private static List<Parsed> Parse(byte[] blob, int count)
        {
            var stringTableStart = count * 18;
            var stringTableLength = BitConverter.ToInt32(blob, stringTableStart);

            string StringAt(int offset)
            {
                var start = stringTableStart + offset;
                var end = start;
                while (blob[end] != 0)
                {
                    end++;
                }

                return Encoding.ASCII.GetString(blob, start, end - start);
            }

            var result = new List<Parsed>();
            var cursor = 0;
            while (cursor < count)
            {
                var offset = cursor * 18;
                var shortName = Encoding.ASCII.GetString(blob, offset, 8).TrimEnd('\0');
                var name = shortName;
                if (BitConverter.ToUInt32(blob, offset) == 0)
                {
                    var nameOffset = BitConverter.ToInt32(blob, offset + 4);
                    if (nameOffset < stringTableLength && nameOffset >= 0)
                    {
                        name = StringAt(nameOffset);
                    }
                }

                var auxCount = blob[offset + 17];
                result.Add(new Parsed(
                    offset,
                    name,
                    BitConverter.ToUInt32(blob, offset + 8),
                    BitConverter.ToUInt16(blob, offset + 12),
                    BitConverter.ToUInt16(blob, offset + 14),
                    blob[offset + 16],
                    auxCount));
                cursor += 1 + auxCount;
            }

            return result;
        }
    }
}
