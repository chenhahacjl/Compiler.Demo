using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// Tutorial 样例冒烟测试：构建 samples/samples.cosln（native + dotnet 双后端），
    /// 逐块运行 13 个功能块 exe 并断言输出，触发第二次 build 验证增量 up-to-date，
    /// 并覆盖 Functions 块 entry=run 的带参/无参两种入口路径。
    /// </summary>
    public class SampleSmokeTests
    {
        private static string GetCocDllPath()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "cocoa.dll");
            Assert.True(File.Exists(path), $"CLI assembly not found at '{path}'. Build Cocoa.Compiler first.");
            return path;
        }

        private static string GetTempDir()
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-sample-smoke-tests");
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static string FindTutorialDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "samples");
                if (File.Exists(Path.Combine(candidate, "samples.cosln")))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("samples/samples.cosln not found above test output directory.");
        }

        private static (int ExitCode, string Stdout, string Stderr) RunCli(string args)
        {
            var psi = new ProcessStartInfo("dotnet", $"\"{GetCocDllPath()}\" {args}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(120000);
            return (process.ExitCode, stdout, stderr);
        }

        private static void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var target = Path.Combine(dest, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }

        private static string RunNative(string exePath, params string[] arguments)
        {
            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var arg in arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            process.WaitForExit(30000);
            copyTask.Wait();
            return Encoding.Unicode.GetString(output.ToArray());
        }

        /// <summary>功能块 → Tutorial 主题组（Basics / Data / Dialects / Interop）。</summary>
        private static readonly Dictionary<string, string> BlockGroups = new()
        {
            ["HelloWorld"] = "Basics",
            ["Types"] = "Basics",
            ["ControlFlow"] = "Basics",
            ["Functions"] = "Basics",
            ["Arrays"] = "Data",
            ["Strings"] = "Data",
            ["Doubles"] = "Data",
            ["ByteArrays"] = "Data",
            ["Enums"] = "Data",
            ["CsStyle"] = "Dialects",
            ["TopLevelFunctions"] = "Dialects",
            ["CSharpDialect"] = "Dialects",
            ["Interop"] = "Interop",
        };

        private static string BlockExe(string runDir, string block)
            => Path.Combine(runDir, "Tutorial", BlockGroups[block], block, "out", block + ".exe");

        private static void AssertBlockOutput(
            Func<string, string[], string> run, string runDir, string block, string[] expectedLines, string[] args)
        {
            var stdout = run(BlockExe(runDir, block), args);
            foreach (var line in expectedLines)
            {
                Assert.Contains(line, stdout);
            }
        }

        private static void AssertBlocks(Func<string, string[], string> run, string runDir)
        {
            AssertBlockOutput(run, runDir, "HelloWorld", new[] { "Hello, Cocoa!", "42" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, "Types", new[] { "42", "11", "53", "Alice", "3.14", "True", "Cocoa Lang" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, "ControlFlow", new[] { "positive", "zero", "negative", "15", "3", "1", "12", "2" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, "Arrays", new[] { "10", "99", "30", "3", "139", "True", "False" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, "Strings", new[] { "5", "h", "101", "ell", "ell!", "a", "True", "b", "False" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, "Enums", new[] { "0", "1", "2", "True", "False", "404", "99" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, "ByteArrays", new[] { "65", "200", "255", "44", "True", "255" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, "Doubles", new[] { "3.14", "3.75", "2.5", "5", "5.5", "False", "3", "1.5", "2.5", "4" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, "Interop", new[] { "True", "True" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, "CsStyle", new[] { "42", "25", "10", "Hi, Cocoa (3)", "C# style", "1" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, "TopLevelFunctions", new[] { "5", "30", "3", "24", "ababab", "42" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, "CSharpDialect", new[] { "4", "100", "i = 0", "i = 1", "i = 2", "5", "40", "one", "few", "few" }, Array.Empty<string>());
        }

        private static void AssertFunctionsEntry(Func<string, string[], string> run, string runDir)
        {
            // 带参运行：entry=run 接收命令行参数
            var withArgs = run(BlockExe(runDir, "Functions"), new[] { "alpha", "beta" });
            Assert.Contains("42", withArgs);
            Assert.Contains("9", withArgs);
            Assert.Contains("120", withArgs);
            Assert.Contains("Hello, Cocoa!", withArgs);
            Assert.Contains("2", withArgs);
            Assert.Contains("alpha", withArgs);
            Assert.Contains("beta", withArgs);
            Assert.DoesNotContain("not used when entry = run", withArgs);

            // 无参运行：args.Length = 0
            var noArgs = run(BlockExe(runDir, "Functions"), Array.Empty<string>());
            Assert.Contains("42", noArgs);
            Assert.Contains("120", noArgs);
            Assert.DoesNotContain("alpha", noArgs);
        }

        private static string Build(string backend, string? dotnetRuntime = null)
        {
            var runDir = Path.Combine(GetTempDir(), Guid.NewGuid().ToString("N"));
            CopyDirectory(FindTutorialDir(), runDir);

            var arg = $"build \"{Path.Combine(runDir, "samples.cosln")}\" -b {backend}"
                + (dotnetRuntime == null ? "" : $" --dotnet-runtime {dotnetRuntime}");
            var first = RunCli(arg);
            Assert.True(first.ExitCode == 0, $"first build failed: {first.Stdout}{first.Stderr}");

            var second = RunCli(arg);
            Assert.True(second.ExitCode == 0, $"second build failed: {second.Stdout}{second.Stderr}");
            Assert.Contains("up to date", second.Stdout);

            return runDir;
        }

        /// <summary>
        /// 容错构建（6e-M21）：聚合解决方案含 native 后端暂不支持的项目
        /// （Libraries/NetLibrary 的 dll 库、Classes/CSharpClass 的对象创建/OOP）——允许整体退出非零，
        /// 仅要求 Tutorial 各功能块产物生成成功。
        /// </summary>
        private static string BuildTolerant(string backend, string? dotnetRuntime = null)
        {
            var runDir = Path.Combine(GetTempDir(), Guid.NewGuid().ToString("N"));
            CopyDirectory(FindTutorialDir(), runDir);

            var arg = $"build \"{Path.Combine(runDir, "samples.cosln")}\" -b {backend}"
                + (dotnetRuntime == null ? "" : $" --dotnet-runtime {dotnetRuntime}");
            var first = RunCli(arg);
            Assert.True(File.Exists(BlockExe(runDir, "HelloWorld")), $"tutorial blocks missing after build: {first.Stdout}{first.Stderr}");

            return runDir;
        }

        private static string RunDirectExe(string exePath, params string[] arguments)
        {
            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            copyTask.Wait();
            Assert.True(process.ExitCode == 0, $"exe {exePath} failed with exit {process.ExitCode}; stderr=[{stderr}]");
            Assert.True(string.IsNullOrEmpty(stderr), $"host stderr for {exePath}: {stderr}");
            var bytes = output.ToArray();
            // netfx/apphost 的 Console 默认用系统代码页（ASCII/UTF-8）；Cocoa native 后端才是 UTF-16
            var encoding = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE
                ? Encoding.Unicode
                : Encoding.UTF8;
            return encoding.GetString(bytes);
        }

        private static string BuildNetFx(string dotnetRuntime)
        {
            var runDir = Path.Combine(GetTempDir(), Guid.NewGuid().ToString("N"));
            CopyDirectory(FindTutorialDir(), runDir);

            var arg = $"build \"{Path.Combine(runDir, "samples.cosln")}\" -b dotnet --dotnet-runtime {dotnetRuntime}";
            var first = RunCli(arg);
            Assert.True(first.ExitCode == 0, $"first netfx build failed: {first.Stdout}{first.Stderr}");

            var second = RunCli(arg);
            Assert.True(second.ExitCode == 0, $"second netfx build failed: {second.Stdout}{second.Stderr}");
            Assert.Contains("up to date", second.Stdout);

            return runDir;
        }

        [Fact]
        public void Tutorial_Native_AllBlocks_BuildAndRun()
        {
            // native 后端暂不支持 OOP（new）/dll 库项目：聚合构建容错，仅要求 Tutorial 各块可用
            var runDir = BuildTolerant(backend: "native");
            AssertBlocks(RunNative, runDir);
            AssertFunctionsEntry(RunNative, runDir);
        }

        [Fact]
        public void Tutorial_DotNet_AllBlocks_BuildAndRun()
        {
            // 样例 coproj 默认 dotnetRuntime = net48（netfx）；netcore 分支需显式覆盖回 net9.0。
            // netcore 产物含原生 apphost：直接运行（双击等价）。
            var runDir = Build(backend: "dotnet", dotnetRuntime: "net9.0")!;
            AssertBlocks(RunDirectExe, runDir);
            AssertFunctionsEntry(RunDirectExe, runDir);
        }

        [Fact]
        public void Tutorial_NetFx_AllBlocks_BuildAndDirectRun()
        {
            // 样例 coproj 默认 dotnetRuntime = net48，构建 netfx 后直接运行
            var runDir = BuildNetFx("net48");
            AssertBlocks(RunDirectExe, runDir);
            AssertFunctionsEntry(RunDirectExe, runDir);
        }
    }
}