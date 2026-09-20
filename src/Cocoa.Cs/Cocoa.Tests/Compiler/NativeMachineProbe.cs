using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Targeting;
using System;
using System.IO;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// M5-a4 切片 2 金签探针：corpus[0]/[1]（单函数，entry==Main）→ EmitNative →
    /// 从 PE EntryPoint RVA 提取 .text 段函数机器码 hex，固化进自持 LirToAssembler 测试。
    /// </summary>
    public class NativeMachineProbe
    {
        private readonly ITestOutputHelper _output;

        public NativeMachineProbe(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Dump_Corpus_Machine()
        {
            var corpus = GoldenGenerator.BinderBoundCorpus;
            for (var i = 0; i < 2; i++)
            {
                var exe = Path.Combine(Path.GetTempPath(), "probe-" + i + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".exe");
                TargetPlatform.TryParse("windows-x64", out var platform);
                var compilation = Compilation.Create(SyntaxTree.Parse(corpus[i]));
                var diags = compilation.EmitNative("probe", exe, platform);
                Assert.True(diags.IsEmpty, string.Join("\n", diags));

                var text = ExtractEntryCode(exe);
                _output.WriteLine($"===== corpus[{i}] entry code =====");
                _output.WriteLine(text);
            }
        }

        private static string ExtractEntryCode(string exePath)
        {
            var bytes = File.ReadAllBytes(exePath);
            var peOff = BitConverter.ToInt32(bytes, 0x3C);
            var machine = BitConverter.ToUInt16(bytes, peOff + 4);
            var is64 = machine == 0x8664;
            var optOff = peOff + 24;
            var magic = BitConverter.ToUInt16(bytes, optOff);
            var optSize = magic == 0x20B ? 240 : 224;
            var numSections = BitConverter.ToUInt16(bytes, peOff + 6);
            var entryRva = magic == 0x20B
                ? BitConverter.ToInt32(bytes, optOff + 16)
                : BitConverter.ToInt32(bytes, optOff + 16);

            var sectionOff = optOff + optSize;
            for (var s = 0; s < numSections; s++)
            {
                var sec = sectionOff + s * 40;
                var vSize = BitConverter.ToInt32(bytes, sec + 8);
                var vAddr = BitConverter.ToInt32(bytes, sec + 12);
                var rawSize = BitConverter.ToInt32(bytes, sec + 16);
                var rawPtr = BitConverter.ToInt32(bytes, sec + 20);
                if (entryRva >= vAddr && entryRva < vAddr + Math.Max(vSize, rawSize))
                {
                    var off = rawPtr + (entryRva - vAddr);
                    var len = is64 ? 200 : 160;
                    var sb = new StringBuilder();
                    for (var i = 0; i < len && off + i < bytes.Length; i++)
                    {
                        sb.Append(bytes[off + i].ToString("x2"));
                    }

                    return sb.ToString();
                }
            }

            return "<no-section>";
        }
    }
}