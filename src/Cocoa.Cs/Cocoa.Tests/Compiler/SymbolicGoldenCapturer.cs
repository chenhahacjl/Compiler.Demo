using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Targeting;
using System;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// M5-a4 续作：把 13 corpus 的 C# 符号化 dump 落盘为参考金签（COCOA_DUMP_SYM），
    /// 供自举 LirToAssembler 差分驱动。
    /// </summary>
    public class SymbolicGoldenCapturer
    {
        [Fact]
        public void Capture_Corpus_SymbolicDump()
        {
            var sb = new StringBuilder();
            for (var i = 0; i < GoldenGenerator.BinderBoundCorpus.Length; i++)
            {
                var dump = DumpCorpus(GoldenGenerator.BinderBoundCorpus[i]);
                sb.Append("===== corpus[").Append(i).Append("] =====\n");
                sb.Append(dump);
            }

            var path = Path.Combine(Path.GetTempPath(), "cocoa-corpus-sym-ref.txt");
            File.WriteAllText(path, sb.ToString());
        }

        private static string DumpCorpus(string source)
        {
            const string target = "windows-x64";
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create(SyntaxTree.Parse(source));
            var exePath = Path.Combine(Path.GetTempPath(), "corpus-sym-" + Guid.NewGuid().ToString("N") + ".exe");
            var dumpFile = Path.Combine(Path.GetTempPath(), "cocoa-sym-x64.txt");

            var previous = Environment.GetEnvironmentVariable("COCOA_DUMP_SYM");
            Environment.SetEnvironmentVariable("COCOA_DUMP_SYM", "1");
            try
            {
                File.Delete(dumpFile);
                var diagnostics = compilation.EmitNative("test", exePath, platform);
                Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics));
                Assert.True(File.Exists(dumpFile), "dump missing");
                return File.ReadAllText(dumpFile);
            }
            finally
            {
                if (previous == null)
                {
                    Environment.SetEnvironmentVariable("COCOA_DUMP_SYM", null);
                }
                else
                {
                    Environment.SetEnvironmentVariable("COCOA_DUMP_SYM", previous);
                }
            }
        }
    }
}