using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using Cocoa.Targeting;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 阶段 7 增量五（M5-b0）：自举测试项目（Cocoa.Co/Cocoa.Tests）的 C# 引导 harness。
    /// 源级编译 Cocoa.Compiler/**/*.co + Cocoa.Tests/**/*.co 为单程序 → Evaluator/Native 运行 →
    /// 断言 Main 返回 0（有失败返回 1）且输出含 "FAIL: 0"。
    /// 与 native `cocoa build/run Cocoa.Tests.cosln` 构成双轨：C# 引导（快反馈）+ native 自举门禁。
    /// </summary>
    public class CocoaCoSelfHostedTests
    {
        [Fact]
        public void CocoaTests_SourceCompiled_Evaluator_ExitZero()
        {
            var (compileErrors, output, exitValue) = RunSelfHosted(null);

            Assert.True(compileErrors.Count == 0, "COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));
            Assert.True(exitValue.HasValue, "Main 未返回退出码（求值中断）。\n输出:\n" + output);
            Assert.True(exitValue!.Value == 0, $"自举测试 Main 返回 {exitValue.Value}（≠0）。\n输出:\n{output}");
            Assert.Contains("FAIL: 0", output.Replace("\0", "").Replace("\r\n", "\n"));
        }

        [Fact]
        public void CocoaTests_SourceCompiled_Native_ExitZero()
        {
            var root = RepoRoot();
            var (compileErrors, output, exitValue) = RunSelfHostedNative(root, null);

            Assert.True(compileErrors.Count == 0, "NATIVE-COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));
            Assert.True(exitValue == 0, $"自举测试 native exe 退出码 {exitValue}（≠0）。\n输出:\n{output}");
            Assert.Contains("FAIL: 0", output.Replace("\0", "").Replace("\r\n", "\n"));
        }

        [Fact]
        public void CocoaTests_Native_Perf_WithinBudget()
        {
            // 移植 Cocoa.Cs/LexerPerformanceTests：1MB 语料 lex 秒级护栏（>5s 即 O(n²) 回归）。
            // 自举测试 Main 按 --perf 门控；只计时 native exe 运行；语料经 SDK StringBuilder 高效构建。
            var root = RepoRoot();
            var (compileErrors, output, exitValue, elapsed) = RunSelfHostedNativeTimed(root, new[] { "--perf" });

            Assert.True(compileErrors.Count == 0, "NATIVE-COCOMPILE-ERROR: " + string.Join(" | ", compileErrors));
            Assert.True(exitValue == 0, $"自举性能测试 native exe 退出码 {exitValue}（≠0）。\n输出:\n{output}");
            Assert.True(elapsed < TimeSpan.FromSeconds(5), $"1MB corpus lexed in {elapsed} (>5s, O(n^2) regression?)");
        }

        private static ImmutableArray<SyntaxTree> BuildTrees()
        {
            var root = RepoRoot();
            var compilerDir = Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Compiler");
            var testsDir = Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Tests");
            var files = Directory.GetFiles(compilerDir, "*.co", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(testsDir, "*.co", SearchOption.AllDirectories))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            var trees = ImmutableArray.CreateBuilder<SyntaxTree>();
            foreach (var file in files)
            {
                trees.Add(SyntaxTree.Parse(File.ReadAllText(file)));
            }

            return trees.ToImmutable();
        }

        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private static (List<string> CompileErrors, string Output, int? ExitValue) RunSelfHosted(string[]? args)
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);

                var compilation = Compilation.Create("Main", References(), BuildTrees().ToArray());
                var result = args == null
                    ? compilation.Evaluate(new Dictionary<VariableSymbol, object>())
                    : compilation.Evaluate(args, new Dictionary<VariableSymbol, object>());

                var errors = result.Diagnostics.HasErrors()
                    ? result.Diagnostics.Select(d => d.Message).ToList()
                    : new List<string>();

                int? exitValue = result.Value as int?;
                var output = writer.ToString().Replace("\r\n", "\n").TrimEnd('\n');
                return (errors, output, exitValue);
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        private static (List<string> CompileErrors, string Output, int ExitValue) RunSelfHostedNative(string root, string[]? args)
        {
            var (compileErrors, output, exitValue, _) = RunSelfHostedNativeTimed(root, args);
            return (compileErrors, output, exitValue);
        }

        private static (List<string> CompileErrors, string Output, int ExitValue, TimeSpan Elapsed) RunSelfHostedNativeTimed(string root, string[]? args)
        {
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-selftest", "cocoa-tests-" + Guid.NewGuid().ToString("N") + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);

            var compilation = Compilation.Create("Main", References(), BuildTrees().ToArray());
            var diagnostics = compilation.EmitNative("Main", exePath, new TargetPlatform(TargetOS.Windows, Architecture.X64));

            if (diagnostics.HasErrors())
            {
                return (diagnostics.Select(d => d.Message).ToList(), "", -1, TimeSpan.Zero);
            }

            var psi = new System.Diagnostics.ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (args != null)
            {
                foreach (var a in args)
                {
                    psi.ArgumentList.Add(a);
                }
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using var process = System.Diagnostics.Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(120000);
            stopwatch.Stop();

            var output = stdout.Replace("\0", "").Replace("\r\n", "\n").TrimEnd('\n');
            return (new List<string>(), output, process.ExitCode, stopwatch.Elapsed);
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
    }
}