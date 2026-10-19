using Cocoa.CodeGen.ObjectFormat.PE;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeGen.PE
{
    /// <summary>原生 PDB 7.0 写入器结构验证：MSF 容器 + 流目录 + DS/符号流内容。</summary>
    public class PdbWriterTests
    {
        [Fact]
        public void Build_Emits_ValidMsf_With_ExpectedStreams()
        {
            var functions = new[]
            {
                new PdbWriter.PdbFunctionSymbol("Main", 0x1000, 0x80),
                new PdbWriter.PdbFunctionSymbol("Add", 0x1080, 0x30),
            };
            var bytes = PdbWriter.Build("probe", functions, 0x2000);
            Assert.NotNull(bytes);
            var pdb = bytes!;

            const int blockSize = 0x1000;
            // MSF 魔数："Microsoft C/C++ MSF 7.00\r\n\x1aDS\0\0"
            Assert.Equal("Microsoft C/C++ MSF 7.00", Encoding.ASCII.GetString(pdb, 0, 24));
            Assert.Equal(new byte[] { 0x0D, 0x0A, 0x1A, (byte)'D', (byte)'S', 0, 0 }, pdb.Skip(24).Take(7).ToArray());

            // 块映射[0] → 目录所在块
            var dirBlock = BitConverter.ToInt32(pdb, blockSize);
            Assert.Equal(2, dirBlock);
            var dirOff = dirBlock * blockSize;

            // 流目录：5 流（DS/TPI/DBI/IPI/Symbols）
            var streamCount = BitConverter.ToInt32(pdb, dirOff);
            Assert.Equal(5, streamCount);
            var sizes = new List<int>();
            for (var i = 0; i < streamCount; i++)
            {
                sizes.Add(BitConverter.ToInt32(pdb, dirOff + 4 + i * 4));
            }

            Assert.Equal(5, sizes.Count);
            Assert.True(sizes[0] >= 28);   // DS
            Assert.True(sizes[1] >= 48);   // TPI
            Assert.True(sizes[2] >= 60);   // DBI
            Assert.True(sizes[4] > 0);     // Symbols
        }

        [Fact]
        public void MakeGuid_Is_Deterministic()
        {
            var g1 = PdbWriter.MakeGuid("probe");
            var g2 = PdbWriter.MakeGuid("probe");
            Assert.Equal(g1, g2);
            Assert.NotEqual(PdbWriter.MakeGuid("probe"), PdbWriter.MakeGuid("other"));
        }

        [Fact]
        public void Build_SymbolsStream_Contains_FunctionNames()
        {
            var functions = new[]
            {
                new PdbWriter.PdbFunctionSymbol("Main", 0x1000, 0x80, 5),
                new PdbWriter.PdbFunctionSymbol("Helper", 0x1080, 0x40, 12),
            };
            var bytes = PdbWriter.Build("probe", functions, 0x2000)!;

            // 解析 MSF：取 Symbols 流（索引 4）内容，扫描 S_GPROC32 记录里的函数名
            var symbolStream = ReadStream(bytes, 4);
            var text = Encoding.ASCII.GetString(symbolStream);
            Assert.True(text.Contains("Main", StringComparison.Ordinal), "Main 缺失：stream4=" + BitConverter.ToString(symbolStream).Substring(0, Math.Min(120, symbolStream.Length * 3)));
            Assert.True(text.Contains("Helper", StringComparison.Ordinal), "Helper 缺失");
            Assert.True(text.Contains("probe", StringComparison.Ordinal), "S_OBJNAME 缺失");

            // S_LINES（0x1105）行号表存在：fileId=0、首行段内偏移 0、语句行 5（bit31 标记）
            var hasLines = false;
            for (var i = 0; i + 4 <= symbolStream.Length; i += 4)
            {
                if (symbolStream[i + 2] == 0x05 && symbolStream[i + 3] == 0x11)
                {
                    hasLines = true;
                    Assert.Equal(0u, BitConverter.ToUInt32(symbolStream, i + 4)); // fileId
                    Assert.Equal(0u, BitConverter.ToUInt32(symbolStream, i + 12)); // 首行段内偏移
                    var line = BitConverter.ToUInt32(symbolStream, i + 16);         // 首行行号
                    Assert.True((line & 0x80000000u) != 0, "语句行标记缺失");
                    Assert.Equal(5u, line & 0x7FFFFFFFu);
                    break;
                }
            }

            Assert.True(hasLines, "S_LINES 记录缺失");
        }

        private static byte[] ReadStream(byte[] pdb, int streamIndex)
        {
            const int blockSize = 0x1000;
            var dirBlock = BitConverter.ToInt32(pdb, blockSize);
            var dirOff = dirBlock * blockSize;
            var count = BitConverter.ToInt32(pdb, dirOff);
            var sizes = new int[count];
            for (var i = 0; i < count; i++)
            {
                sizes[i] = BitConverter.ToInt32(pdb, dirOff + 4 + i * 4);
            }

            var blockCursor = dirOff + 4 + count * 4;
            // 跳过前 streamIndex 个流的块号
            var blocksPerStream = new List<int>();
            var blocks = new List<List<int>>();
            for (var i = 0; i < count; i++)
            {
                var n = (sizes[i] + blockSize - 1) / blockSize;
                var list = new List<int>();
                for (var k = 0; k < n; k++)
                {
                    list.Add(BitConverter.ToInt32(pdb, blockCursor));
                    blockCursor += 4;
                }

                blocks.Add(list);
            }

            var result = new byte[sizes[streamIndex]];
            var streamBlocks = blocks[streamIndex];
            for (var k = 0; k < streamBlocks.Count; k++)
            {
                var src = streamBlocks[k] * blockSize;
                var dst = k * blockSize;
                var len = Math.Min(blockSize, sizes[streamIndex] - dst);
                Array.Copy(pdb, src, result, dst, len);
            }

            return result;
        }
    }
}
