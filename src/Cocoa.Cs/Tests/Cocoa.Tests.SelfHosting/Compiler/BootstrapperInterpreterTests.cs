using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 阶段 7 增量五 M5-a2：B0 端到端——自举编译器 Cli（Cocoa.Co.cosln）的 `run` 子命令
    /// 经自举 Interpreter 执行程序。B0 stdout + 退出码与 C# `Compilation.Evaluate`
    /// （捕获的 Console 输出 + 返回值）逐字节一致（dotnet + native 双后端）。
    /// 语料：函数自足程序（含 Console.WriteLine + 返回值；Interpreter 本轮不支持全局变量）。
    /// </summary>
    public class BootstrapperInterpreterTests
    {
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

        private static (int ExitCode, string Stdout, string Stderr) InvokeCli(string args)
        {
            var cli = Path.Combine(AppContext.BaseDirectory, "cocoa.dll");
            Assert.True(File.Exists(cli), $"CLI assembly not found at '{cli}'. Build Cocoa.Cli first.");
            var psi = new ProcessStartInfo("dotnet", $"\"{cli}\" {args}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);
            return (process.ExitCode, stdout, stderr);
        }

        private static (int ExitCode, string Stdout) RunCli(string exePath, string arguments, string backend)
        {
            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                Arguments = arguments,
            };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var task = process.StandardOutput.BaseStream.CopyToAsync(output);
            process.WaitForExit(30_000);
            task.Wait();
            var bytes = output.ToArray();
            var text = (backend == "native" ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes))
                .Replace("\0", "").Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
            return (process.ExitCode, text);
        }

        [Theory]
        [InlineData("dotnet", " --dotnet-runtime net9.0")]
        [InlineData("native", "")]
        public void B0_Run_Matches_CShaEvaluator(string backend, string runtimeArgs)
        {
            var solution = Path.Combine(RepoRoot(), "src", "Cocoa.Co", "Cocoa.Co.cosln");
            var source =
                "using System\n\n" +
                "function Sum(a: i32, b: i32): i32\n{\n    return a + b\n}\n\n" +
                "function Fib(n: i32): i32\n{\n    if n <= 1\n    {\n        return n\n    }\n\n    return Fib(n - 1) + Fib(n - 2)\n}\n\n" +
                "function Main(): i32\n{\n    Console.WriteLine(Sum(1, 2))\n    Console.WriteLine(Sum(20, 22))\n    Console.WriteLine(Fib(10))\n" +
                "    Console.WriteLine(System.Math.Max(3, 7))\n" +
                "    Console.WriteLine(System.Math.Min(3, 7))\n" +
                "    Console.WriteLine(System.Math.Abs(-5))\n" +
                "    Console.WriteLine(System.Math.Pow(2, 3))\n" +
                "    Console.WriteLine(System.Int32.Parse(\"42\"))\n" +
                "    return Sum(3, 4)\n}\n";

            // C# 基准：Console 输出 + 返回值
            var compilation = Compilation.Create(SyntaxTree.Parse(source));
            var original = Console.Out;
            string csharpStdout;
            string csharpExit;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                csharpStdout = writer.ToString().Replace("\r\n", "\n").TrimEnd('\n');
                csharpExit = result.Value?.ToString() ?? "<null>";
            }
            finally
            {
                Console.SetOut(original);
            }

            // 构建 B0
            var (buildExit, buildOut, buildErr) = InvokeCli($"build \"{solution}\" --no-incremental -b {backend}{runtimeArgs}");
            Assert.True(buildExit == 0, $"build failed ({backend}). stdout=[{buildOut}] stderr=[{buildErr}]");

            var sourcePath = Path.Combine(Path.GetTempPath(), "cocoa-b0-run-" + Guid.NewGuid().ToString("N") + ".co");
            File.WriteAllText(sourcePath, source);
            try
            {
                var outDir = Path.Combine(RepoRoot(), "src", "Cocoa.Co", "Cli", "out");
                var exe = Path.Combine(outDir, "Cocoa.Cli.exe");
                Assert.True(File.Exists(exe), $"B0 exe not produced at {exe}");

                var runExe = exe;
                if (backend == "native")
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), "cocoa-b0-run-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);
                    runExe = Path.Combine(tempDir, "Cocoa.Cli.exe");
                    File.Copy(exe, runExe, overwrite: true);
                }

                var (b0Exit, b0Stdout) = RunCli(runExe, $"run \"{sourcePath}\"", backend);
                Assert.True(csharpExit == b0Exit.ToString(), $"B0 exit={b0Exit}, C# exit={csharpExit}");

                Assert.True(b0Stdout == csharpStdout,
                    $"B0 run stdout mismatch ({backend}).\nC#  : [{csharpStdout}]\nB0  : [{b0Stdout}]");
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }
    }
}