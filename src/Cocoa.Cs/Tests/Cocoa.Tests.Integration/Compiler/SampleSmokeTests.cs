using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.Integration.Compiler
{
    /// <summary>
    /// Tutorial 样例冒烟测试：构建 samples/samples.cosln（native + dotnet 双后端），
    /// 逐块运行 13 个功能块 exe 并断言输出，触发第二次 build 验证增量 up-to-date，
    /// 并覆盖 Functions 块 entry=run 的带参/无参两种入口路径。
    /// 输出布局：bin/&lt;Config&gt;/native/&lt;arch&gt; · bin/&lt;Config&gt;/managed/&lt;tfm&gt;（方案 A）。
    /// </summary>
    public class SampleSmokeTests
    {
        private static string GetCocDllPath()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "cocoa.dll");
            Assert.True(File.Exists(path), $"CLI assembly not found at '{path}'. Build Cocoa.Cli first.");
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

            using var output = new MemoryStream();
            using var process = Process.Start(psi)!;
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
            ["Interop"] = "Interop",
        };

        /// <summary>方案 A 产物相对子路径（Config=Debug）。</summary>
        private const string NativeArtifact = "bin\\Debug\\native\\x64";
        private const string ManagedNet9Artifact = "bin\\Debug\\managed\\net9.0";
        private const string ManagedNet48Artifact = "bin\\Debug\\managed\\net48";

        private static string BlockExe(string runDir, string block, string artifactDir)
            => Path.Combine(runDir, "Tutorial", BlockGroups[block], block, artifactDir, block + ".exe");

        private static void AssertBlockOutput(
            Func<string, string[], string> run, string runDir, string artifactDir, string block, string[] expectedLines, string[] args)
        {
            var stdout = run(BlockExe(runDir, block, artifactDir), args);
            foreach (var line in expectedLines)
            {
                Assert.Contains(line, stdout);
            }
        }

        private static void AssertBlocks(Func<string, string[], string> run, string runDir, string artifactDir)
        {
            AssertBlockOutput(run, runDir, artifactDir, "HelloWorld", new[] { "Hello, Cocoa!", "42" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, artifactDir, "Types", new[] { "42", "11", "53", "Alice", "3.14", "True", "Cocoa Lang" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, artifactDir, "ControlFlow", new[] { "positive", "zero", "negative", "15", "3", "1", "12", "2" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, artifactDir, "Arrays", new[] { "10", "99", "30", "3", "139", "True", "False" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, artifactDir, "Strings", new[] { "5", "h", "101", "ell", "ell!", "a", "True", "b", "False" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, artifactDir, "Enums", new[] { "0", "1", "2", "True", "False", "404", "99" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, artifactDir, "ByteArrays", new[] { "65", "200", "255", "44", "True", "255" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, artifactDir, "Doubles", new[] { "3.14", "3.75", "2.5", "5", "5.5", "False", "3", "1.5", "2.5", "4" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, artifactDir, "Interop", new[] { "True", "True" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, artifactDir, "CsStyle", new[] { "42", "25", "10", "Hi, Cocoa (3)", "C# style", "1" }, Array.Empty<string>());
            AssertBlockOutput(run, runDir, artifactDir, "TopLevelFunctions", new[] { "5", "30", "3", "24", "ababab", "42" }, Array.Empty<string>());
        }

        private static void AssertFunctionsEntry(Func<string, string[], string> run, string runDir, string artifactDir)
        {
            // 带参运行：entry=run 接收命令行参数
            var withArgs = run(BlockExe(runDir, "Functions", artifactDir), new[] { "alpha", "beta" });
            Assert.Contains("42", withArgs);
            Assert.Contains("9", withArgs);
            Assert.Contains("120", withArgs);
            Assert.Contains("Hello, Cocoa!", withArgs);
            Assert.Contains("2", withArgs);
            Assert.Contains("alpha", withArgs);
            Assert.Contains("beta", withArgs);
            Assert.DoesNotContain("not used when entry = run", withArgs);

            // 无参运行：args.Length = 0
            var noArgs = run(BlockExe(runDir, "Functions", artifactDir), Array.Empty<string>());
            Assert.Contains("42", noArgs);
            Assert.Contains("120", noArgs);
            Assert.DoesNotContain("alpha", noArgs);
        }

        /// <summary>常规构建：随附参数返回 (runDir, artifactDir)，断言二次构建 up to date。</summary>
        private static (string RunDir, string ArtifactDir) Build(string backend, string? dotnetRuntime = null)
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

            var artifactDir = backend == "native"
                ? NativeArtifact
                : dotnetRuntime?.StartsWith("net8") == true || (dotnetRuntime?.StartsWith("net9") == true)
                    ? ManagedNet9Artifact
                    : ManagedNet48Artifact;
            return (runDir, artifactDir);
        }

        /// <summary>
        /// 容错构建（6e-M21）：聚合解决方案含 native 后端暂不支持的项目
        /// （Libraries/NetLibrary 的 dll 库、Classes/CSharpClass 的对象创建/OOP）——允许整体退出非零，
        /// 仅要求 Tutorial 各功能块产物生成成功。
        /// </summary>
        private static (string RunDir, string ArtifactDir) BuildTolerant(string backend, string? dotnetRuntime = null)
        {
            var runDir = Path.Combine(GetTempDir(), Guid.NewGuid().ToString("N"));
            CopyDirectory(FindTutorialDir(), runDir);

            var arg = $"build \"{Path.Combine(runDir, "samples.cosln")}\" -b {backend}"
                + (backend == "native" ? " --platform x64" : "")
                + (dotnetRuntime == null ? "" : $" --dotnet-runtime {dotnetRuntime}");
            var first = RunCli(arg);
            Assert.True(File.Exists(BlockExe(runDir, "HelloWorld", NativeArtifact)), $"tutorial blocks missing after build: {first.Stdout}{first.Stderr}");

            return (runDir, NativeArtifact);
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

        private static (string RunDir, string ArtifactDir) BuildNetFx(string dotnetRuntime)
        {
            var runDir = Path.Combine(GetTempDir(), Guid.NewGuid().ToString("N"));
            CopyDirectory(FindTutorialDir(), runDir);

            var arg = $"build \"{Path.Combine(runDir, "samples.cosln")}\" -b managed-fx --dotnet-runtime {dotnetRuntime}";
            var first = RunCli(arg);
            Assert.True(first.ExitCode == 0, $"first netfx build failed: {first.Stdout}{first.Stderr}");

            var second = RunCli(arg);
            Assert.True(second.ExitCode == 0, $"second netfx build failed: {second.Stdout}{second.Stderr}");
            Assert.Contains("up to date", second.Stdout);

            return (runDir, ManagedNet48Artifact);
        }

        [Fact]
        public void Tutorial_Native_AllBlocks_BuildAndRun()
        {
            // native 后端暂不支持 OOP（new）/dll 库项目：聚合构建容错，仅要求 Tutorial 各块可用
            var (runDir, artifactDir) = BuildTolerant(backend: "native");
            AssertBlocks(RunNative, runDir, artifactDir);
            AssertFunctionsEntry(RunNative, runDir, artifactDir);
        }

        [Fact]
        public void Tutorial_DotNet_AllBlocks_BuildAndRun()
        {
            // 样例 coproj 默认 dotnetRuntime = net48（netfx）；netcore 分支需显式覆盖回 net9.0。
            var (runDir, artifactDir) = Build(backend: "managed-net", dotnetRuntime: "net9.0");
            AssertBlocks(RunDirectExe, runDir, artifactDir);
            AssertFunctionsEntry(RunDirectExe, runDir, artifactDir);
        }

        [Fact]
        public void Tutorial_NetFx_AllBlocks_BuildAndDirectRun()
        {
            var (runDir, artifactDir) = BuildNetFx("net48");
            AssertBlocks(RunDirectExe, runDir, artifactDir);
            AssertFunctionsEntry(RunDirectExe, runDir, artifactDir);
        }

        /// <summary>黄金基线（重构阶段 0.1）：samples.cosln 全量项目在 dotnet 后端的产物输出快照。</summary>
        private static string SampleExe(string runDir, string relative, string artifactDir)
            => Path.Combine(runDir, relative, artifactDir, Path.GetFileName(relative) + ".exe");

        private static void AssertExeOutput(Func<string, string[], string> run, string exePath, string[] expectedLines)
        {
            var stdout = run(exePath, Array.Empty<string>());
            foreach (var line in expectedLines)
            {
                Assert.Contains(line, stdout);
            }
        }

        [Fact]
        public void Golden_DotNet_LibrariesAndClasses_Snapshot()
        {
            var (runDir, artifactDir) = Build(backend: "managed-net", dotnetRuntime: "net9.0");

            AssertExeOutput(RunDirectExe, SampleExe(runDir, "Libraries/CoaLibrary/app", artifactDir),
                new[] { "5", "12", "120", "weekend", "Hello, Cocoa" });
            AssertExeOutput(RunDirectExe, SampleExe(runDir, "Libraries/NetLibrary/app", artifactDir),
                new[] { "3", "4", "12" });
            AssertExeOutput(RunDirectExe, SampleExe(runDir, "Classes/CSharpClass", artifactDir),
                new[] { "Bob", "18", "19", "Hello, Alice", "1", "2" });
        }

        [Fact]
        public void Golden_Native_CoaLibrary_Snapshot()
        {
            var (runDir, artifactDir) = BuildTolerant(backend: "native");

            AssertExeOutput(RunNative, SampleExe(runDir, "Libraries/CoaLibrary/app", artifactDir),
                new[] { "5", "12", "120", "weekend", "Hello, Cocoa" });
        }
    }
}