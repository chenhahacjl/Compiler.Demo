using System;
using System.Collections.Generic;
using System.Linq;
using Cocoa.CodeAnalysis.Emit.Native.Runtime.Windows.X64;
using Cocoa.CodeAnalysis.Emit.Native.Runtime.Windows.X86;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native
{
    /// <summary>
    /// 回归测试：stub 的导出名匹配必须覆盖完整名字。
    /// 历史 bug：AnsiWords 只折叠前 8 字节，导致 "GetFileType" 匹配到 "GetFileTime"、
    /// "ReadConsoleW"/"WriteConsoleW" 匹配到 A 版（导入表槽被填成同前缀变体的地址，控制台路径崩溃）。
    /// </summary>
    public class ImportResolverTests
    {
        [Theory]
        [InlineData("GetStdHandle", 3, -1)]             // 12 字节 → 3 dword，末块整 4 字节
        [InlineData("WriteFile", 3, 0xFF)]              // 9 字节 → 3 dword，末块 1 字节
        [InlineData("ReadFile", 2, -1)]                 // 8 字节 → 2 dword，末块整 4 字节
        [InlineData("GetFileType", 3, 0xFFFFFF)]        // 11 字节 → 3 dword，末块 3 字节
        [InlineData("ReadConsoleW", 3, -1)]             // 12 字节 → 3 dword，末块整 4 字节
        [InlineData("WriteConsoleW", 4, 0xFF)]          // 13 字节 → 4 dword，末块 1 字节
        [InlineData("ExitProcess", 3, 0xFFFFFF)]        // 11 字节 → 3 dword，末块 3 字节
        public void X86_AnsiWords_CoversFullName(string name, int partCount, int lastMask)
        {
            var parts = ImportResolverStubEmitterX86.AnsiWords(name);
            var dump = string.Join(";", parts.Select(p => p.Mask.ToString("X8")));
            Assert.True(parts.Count == partCount, $"name={name} parts=[{dump}] (count={parts.Count} expected {partCount})");
            Assert.True(parts[^1].Mask == lastMask, $"name={name} parts=[{dump}] (last mask expected 0x{lastMask:X8})");
        }

        [Fact]
        public void X86_Templates_DistinguishPrefixVariants()
        {
            Assert.False(Match32("GetFileType", "GetFileTime"));   // 前 8 字节 "GETFILET" 相同，第 9 字节不同
            Assert.False(Match32("ReadConsoleW", "ReadConsoleA"));
            Assert.False(Match32("WriteConsoleW", "WriteConsoleA"));
            Assert.True(Match32("GetFileTime", "GetFileTime"));
            Assert.True(Match32("WriteConsoleW", "WriteConsoleW"));
            Assert.True(Match32("GetStdHandle", "GetStdHandle"));  // 唯一前缀，本就正确
            Assert.True(Match32("WriteFile", "WriteFile"));
        }

        [Fact]
        public void X86_Scan_FindsExactNameNotPrefixVariant()
        {
            Assert.Equal("GetFileType", Scan32("GetFileType", new[] { "GetFileAttributesA", "GetFileTime", "GetFileType", "GetStdHandle" }));
            Assert.Equal("ReadConsoleW", Scan32("ReadConsoleW", new[] { "ReadConsoleA", "ReadConsoleW", "ReadFile" }));
            Assert.Equal("WriteConsoleW", Scan32("WriteConsoleW", new[] { "WriteConsoleA", "WriteConsoleW", "WriteFile" }));
            Assert.Equal("WriteFile", Scan32("WriteFile", new[] { "WriteFile", "WriteFileEx", "WriteFileGather" }));
            Assert.Equal("GetStdHandle", Scan32("GetStdHandle", new[] { "GetStdHandle", "GetSystemDefaultLCID" }));
        }

        [Theory]
        [InlineData("GetStdHandle", 2, 0xFFFFFFFFL)]         // 12 字节 → 2 qword，末块 4 字节
        [InlineData("WriteFile", 2, 0xFF)]                   // 9 字节 → 2 qword，末块 1 字节
        [InlineData("ReadFile", 1, -1)]                      // 8 字节 → 1 qword
        [InlineData("GetFileType", 2, 0xFFFFFF)]             // 11 字节 → 2 qword，末块 3 字节
        [InlineData("ReadConsoleW", 2, 0xFFFFFFFFL)]         // 12 字节 → 2 qword，末块 4 字节
        [InlineData("WriteConsoleW", 2, 0xFFFFFFFFFF)]       // 13 字节 → 2 qword，末块 5 字节
        public void X64_AnsiWords_CoversFullName(string name, int partCount, long lastMask)
        {
            var parts = ImportResolverStubEmitter.AnsiWords(name);
            var dump = string.Join(";", parts.Select(p => p.Mask.ToString("X16")));
            Assert.True(parts.Count == partCount, $"name={name} parts=[{dump}] (count={parts.Count} expected {partCount})");
            Assert.True(parts[^1].Mask == lastMask, $"name={name} parts=[{dump}] (last mask expected 0x{lastMask:X16})");
        }

        [Fact]
        public void X64_Templates_DistinguishPrefixVariants()
        {
            Assert.False(Match64("GetFileType", "GetFileTime"));
            Assert.False(Match64("ReadConsoleW", "ReadConsoleA"));
            Assert.False(Match64("WriteConsoleW", "WriteConsoleA"));
            Assert.True(Match64("GetFileTime", "GetFileTime"));
            Assert.True(Match64("WriteConsoleW", "WriteConsoleW"));
            Assert.True(Match64("GetStdHandle", "GetStdHandle"));
        }

        [Fact]
        public void X64_Scan_FindsExactNameNotPrefixVariant()
        {
            Assert.Equal("GetFileType", Scan64("GetFileType", new[] { "GetFileAttributesA", "GetFileTime", "GetFileType", "GetStdHandle" }));
            Assert.Equal("ReadConsoleW", Scan64("ReadConsoleW", new[] { "ReadConsoleA", "ReadConsoleW", "ReadFile" }));
            Assert.Equal("WriteConsoleW", Scan64("WriteConsoleW", new[] { "WriteConsoleA", "WriteConsoleW", "WriteFile" }));
            Assert.Equal("WriteFile", Scan64("WriteFile", new[] { "WriteFile", "WriteFileEx", "WriteFileGather" }));
            Assert.Equal("GetStdHandle", Scan64("GetStdHandle", new[] { "GetStdHandle", "GetSystemDefaultLCID" }));
        }

        // ---- 模拟 stub 运行时名字表扫描（字母序数组，模板逐块比对，取第一个命中）----

        private static bool Match32(string template, string candidate)
        {
            var parts = ImportResolverStubEmitterX86.AnsiWords(template);
            for (var k = 0; k < parts.Count; k++)
            {
                var (word, mask) = parts[k];
                var block = 0;
                for (var j = 0; j < 4; j++)
                {
                    var i = k * 4 + j;
                    var c = i < candidate.Length ? (byte)((byte)candidate[i] | 0x20) : (byte)0;
                    block |= c << (j * 8);
                }

                if ((block & mask) != (word & mask)) return false;
            }

            return true;
        }

        private static string Scan32(string template, string[] names)
        {
            foreach (var candidate in names)
            {
                if (Match32(template, candidate)) return candidate;
            }

            throw new InvalidOperationException("no match");
        }

        private static bool Match64(string template, string candidate)
        {
            var parts = ImportResolverStubEmitter.AnsiWords(template);
            for (var k = 0; k < parts.Count; k++)
            {
                var (word, mask) = parts[k];
                long block = 0;
                for (var j = 0; j < 8; j++)
                {
                    var i = k * 8 + j;
                    var c = i < candidate.Length ? (long)((byte)candidate[i] | 0x20) : 0;
                    block |= c << (j * 8);
                }

                if ((block & mask) != (word & mask)) return false;
            }

            return true;
        }

        private static string Scan64(string template, string[] names)
        {
            foreach (var candidate in names)
            {
                if (Match64(template, candidate)) return candidate;
            }

            throw new InvalidOperationException("no match");
        }
    }
}