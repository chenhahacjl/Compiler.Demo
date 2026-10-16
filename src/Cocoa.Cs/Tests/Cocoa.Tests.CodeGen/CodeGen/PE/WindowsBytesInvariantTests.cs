using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Targeting;
using System;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace Cocoa.Tests.CodeGen.PE
{
    /// <summary>
    /// T3 门禁 2：Windows 产物字节不变（规避 TimeStamp/CheckSum 有意变更）。
    /// 探针程序（覆盖字符串/整数/拼接/浮点/堆分配…）在 T3.1/T3.2 基线（bf36bb18）采集 .text/.data
    /// 原始字节 SHA256 为黄金值；T3.3（ABI 接线 / 运行时拆分）不得改动 Windows 代码生成。
    /// </summary>
    public class WindowsBytesInvariantTests
    {
        private const string ProbeSource = @"using System

function Main()
{
    Console.WriteLine(""ok"")
    Console.WriteLine(42)
    Console.WriteLine(""foo"" + ""bar"")
    Console.WriteLine(3.14)
    var name = ""world""
    Console.WriteLine(name)
    Console.WriteLine(7)
}";

        // bf36bb18 基线采集（2026-10-16 本机 gold 哈希；T3.3 改造前）。
        private const string GoldenTextSha256 = "4EF618F4F15620313D6BB439A0B416EA4EA62ED8EB5DBD3CB32E06F74997B6CA";
        private const string GoldenDataSha256 = "2A95BCFDC960DD204A4AAEB53589E1ED94BABAEFB5B7B7F6348B75C46DF2CCE9";

        [Fact]
        public void WindowsX64_TextAndData_ByteInvariant()
        {
            var syntaxTree = SyntaxTree.Parse(ProbeSource);
            var compilation = Compilation.Create(syntaxTree);
            var outputPath = Path.Combine(Path.GetTempPath(), "cocoa-invariant-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                var diagnostics = compilation.EmitNative("InvariantProbe", outputPath, new TargetPlatform(TargetOS.Windows, Architecture.X64));
                Assert.True(diagnostics.IsEmpty, string.Join("\n", System.Linq.Enumerable.Select(diagnostics, d => d.Message)));

                var bytes = File.ReadAllBytes(outputPath);
                var (textSha, dataSha) = ExtractSectionSha256(bytes, ".text", ".data");

                Assert.Equal(GoldenTextSha256, textSha);
                Assert.Equal(GoldenDataSha256, dataSha);
            }
            finally
            {
                if (File.Exists(outputPath)) File.Delete(outputPath);
            }
        }

        private static (string TextSha256, string DataSha256) ExtractSectionSha256(byte[] image, string first, string second)
        {
            var eLfanew = BitConverter.ToInt32(image, 0x3C);
            var numSections = BitConverter.ToUInt16(image, eLfanew + 4 + 2);
            var optSize = BitConverter.ToUInt16(image, eLfanew + 4 + 16);
            var sectionTable = eLfanew + 4 + 20 + optSize;

            var text = "";
            var data = "";
            for (var i = 0; i < numSections; i++)
            {
                var st = sectionTable + i * 40;
                var name = NameAt(image, st);
                var rawSize = (int)BitConverter.ToUInt32(image, st + 16);
                var rawPtr = (int)BitConverter.ToUInt32(image, st + 20);
                var raw = new byte[rawSize];
                Array.Copy(image, rawPtr, raw, 0, rawSize);
                var hash = Sha256(raw);
                if (name == first) text = hash;
                if (name == second) data = hash;
            }

            Assert.NotEqual("", text);
            Assert.NotEqual("", data);
            return (text, data);
        }

        private static string NameAt(byte[] image, int offset)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < 8; i++)
            {
                var b = image[offset + i];
                if (b == 0) break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        private static string Sha256(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }
    }
}