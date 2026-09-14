using Cocoa.CodeAnalysis;
using Cocoa.Targeting;
using Cocoa.CodeGen.Native;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 阶段 7 增量一 M7-a3：≥1MB 语料秒级性能护栏（验证自举 Lexer 无 O(n²) 回归）。
    /// 计数式主程序（不逐 token 输出），仅打印 token 总数。
    /// 后端选 **native x64**（Evaluator 为字节码解释器，1MB 线性量级即 ~2min，无法作为秒级护栏）。
    /// 只计时 exe 运行（不含构建）；预算 5s——O(n²) 在 1MB 量级为 10^12 次操作，远超预算。
    /// </summary>
    public class LexerPerformanceTests
    {
        private const int LineCount = 100_000;

        [Fact]
        public void OneMegabyteSource_Lexes_WithinBudget_OnNative()
        {
            var corpus = BuildCorpus(LineCount);
            var expectedCount = LineCount * 5; // 每行 x = x + 1 → 5 个 token（EOF 不计入）

            var directory = Path.Combine(Path.GetTempPath(), "cocoa-perf");
            Directory.CreateDirectory(directory);
            var exePath = Path.Combine(directory, "lexperf-" + Guid.NewGuid().ToString("N") + ".exe");

            var compilation = Compilation.Create("Main", References(), BuildTrees(corpus).ToArray());
            var diagnostics = compilation.EmitNative("Main", exePath, new TargetPlatform(TargetOS.Windows, Architecture.X64));
            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            var stopwatch = Stopwatch.StartNew();
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            if (!process.WaitForExit(30_000))
            {
                process.Kill();
                throw new TimeoutException("Native perf exe did not exit in time.");
            }

            outputTask.Wait();
            stopwatch.Stop();

            var stdout = Encoding.Unicode.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(expectedCount.ToString(), stdout.Trim());
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"1MB corpus (100k lines, {expectedCount} tokens) lexed in {stopwatch.Elapsed} (>5s, O(n^2) regression?)");
        }

        private static string BuildCorpus(int n)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < n; i++)
            {
                sb.Append("x = x + 1\n");
            }

            return sb.ToString();
        }

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "src", "Cocoa.SDK", "System.Core", "String.co")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            Assert.NotNull(dir);
            return dir!;
        }

        private static string MainSource(string embedded)
        {
            return "using MiniLexer\nusing System\n\nfunction Main(): i32\n{\n    let lex = MiniLexer.Lexer.Create(\"" + embedded + "\")\n    var count = 0\n    while true\n    {\n        let t = lex.Next()\n        if t.Kind() == \"EOF\"\n        {\n            break\n        }\n        count = count + 1\n    }\n    System.Console.WriteLine(string(count))\n    return 0\n}";
        }

        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private static ImmutableArray<SyntaxTree> BuildTrees(string corpus)
        {
            var embedded = corpus.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
            var lexerCo = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cocoa.Co", "Lexer", "Lexer.co"));
            var tokenCo = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cocoa.Co", "Lexer", "Token.co"));
            return ImmutableArray.Create(SyntaxTree.Parse(lexerCo), SyntaxTree.Parse(tokenCo), SyntaxTree.Parse(MainSource(embedded)));
        }
    }
}
