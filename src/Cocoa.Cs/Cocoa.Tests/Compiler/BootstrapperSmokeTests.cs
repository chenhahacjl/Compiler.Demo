using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// 阶段 7 增量一 M7-a4：B0 骨架冒烟——用 C# 编译器把 `src/Cocoa.Co/Cocoa.Co.coproj`
    /// 构建为可运行的自举词法器（dotnet + native），读入示例文件并逐 token 输出。
    /// 验证自举源码可被 C# 编译器独立构建为可运行二进制（增量一验收口径）。
    /// </summary>
    public class BootstrapperSmokeTests
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

        private static string RunAndCapture(string exePath, string arguments, string backend)
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
            return (backend == "native" ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes))
                .Replace("\r\n", "\n").Replace("\r", "\n");
        }

        [Theory]
        [InlineData("dotnet", " --dotnet-runtime net9.0")]
        [InlineData("native", "")]
        public void B0_BuildsAndPrintsTokensAndTree(string backend, string runtimeArgs)
        {
            var coproj = Path.Combine(RepoRoot(), "src", "Cocoa.Co", "Cocoa.Co.coproj");
            var sample = Path.Combine(RepoRoot(), "samples", "Tutorial", "Basics", "HelloWorld", "main.co");
            Assert.True(File.Exists(sample), $"sample not found: {sample}");

            var (exitCode, stdout, stderr) = InvokeCli($"build \"{coproj}\" --no-incremental -b {backend}{runtimeArgs}");
            Assert.True(exitCode == 0, $"build failed ({backend}). stdout=[{stdout}] stderr=[{stderr}]");

            var outDir = Path.Combine(RepoRoot(), "src", "Cocoa.Co", "out");
            var exe = Path.Combine(outDir, "Cocoa.Co.exe");
            Assert.True(File.Exists(exe), $"B0 exe not produced at {exe}");

            var runExe = exe;
            if (backend == "native")
            {
                // native 产物在工作区目录被本机杀软拦截 → 复制到临时目录运行
                var tempDir = Path.Combine(Path.GetTempPath(), "cocoa-b0-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                runExe = Path.Combine(tempDir, "Cocoa.Co.exe");
                File.Copy(exe, runExe, overwrite: true);
            }

            var output = RunAndCapture(runExe, $"\"{sample}\"", backend);
            Assert.Contains("Keyword using 1:1", output);
            Assert.Contains("Identifier Main 2:10", output);
            Assert.Contains("String \"Cocoa\" 4:32", output);
            Assert.Contains("EOF  6:2", output);

            // M8 扩展：B0 亦为自举 Parser 可执行（读文件 → 打印树 + 诊断）；有效样例零诊断
            Assert.Contains("--- tree ---", output);
            Assert.Contains("(CompilationUnit", output);
            Assert.Contains("(FunctionDeclaration", output);
            Assert.DoesNotContain("error:", output);

            // M9 扩展：B0 亦输出自举 Binder 符号表
            Assert.Contains("--- symbols ---", output);
            Assert.Contains("function Main(): void", output);
            Assert.Contains("main: Main", output);
        }
    }
}
