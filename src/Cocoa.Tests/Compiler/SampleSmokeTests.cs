using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// Tutorial 样例冒烟测试：构建 samples/Tutorial/Tutorial.cosln（native + dotnet 双后端），
    /// 逐块运行 10 个功能块 exe 并断言输出，触发第二次 build 验证增量 up-to-date，
    /// 并覆盖 Functions 块 entry=run 的带参/无参两种入口路径。
    /// </summary>
    public class SampleSmokeTests
    {
        private static string GetCocDllPath()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "coc.dll");
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
                var candidate = Path.Combine(dir.FullName, "samples", "Tutorial");
                if (File.Exists(Path.Combine(candidate, "Tutorial.cosln")))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("samples/Tutorial not found above test output directory.");
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

        private static string RunDotnet(string exePath, params string[] arguments)
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(exePath);
            foreach (var arg in arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            Assert.True(string.IsNullOrEmpty(stderr), $"dotnet host stderr for {exePath}: {stderr}");
            return stdout;
        }

        private static string BlockExe(string runDir, string block)
            => Path.Combine(runDir, block, "out", block + ".exe");

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

        private static string Build(string backend)
        {
            var runDir = Path.Combine(GetTempDir(), Guid.NewGuid().ToString("N"));
            CopyDirectory(FindTutorialDir(), runDir);

            var arg = $"build \"{Path.Combine(runDir, "Tutorial.cosln")}\" -b {backend}";
            var first = RunCli(arg);
            Assert.True(first.ExitCode == 0, $"first build failed: {first.Stdout}{first.Stderr}");

            var second = RunCli(arg);
            Assert.True(second.ExitCode == 0, $"second build failed: {second.Stdout}{second.Stderr}");
            Assert.Contains("up to date", second.Stdout);

            return runDir;
        }

        private static string RunNetFx(string exePath, params string[] arguments)
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
            Assert.True(process.ExitCode == 0, $"netfx exe {exePath} failed with exit {process.ExitCode}; stderr=[{stderr}]");
            var bytes = output.ToArray();
            // netfx 的 Console 默认用系统代码页（ASCII/UTF-8）；Cocoa native 后端才是 UTF-16
            var encoding = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE
                ? Encoding.Unicode
                : Encoding.UTF8;
            return encoding.GetString(bytes);
        }

        private static string BuildNetFx(string dotnetRuntime)
        {
            var runDir = Path.Combine(GetTempDir(), Guid.NewGuid().ToString("N"));
            CopyDirectory(FindTutorialDir(), runDir);

            var arg = $"build \"{Path.Combine(runDir, "Tutorial.cosln")}\" -b dotnet --dotnet-runtime {dotnetRuntime}";
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
            var runDir = Build(backend: "native");
            AssertBlocks(RunNative, runDir);
            AssertFunctionsEntry(RunNative, runDir);
        }

        [Fact]
        public void Tutorial_DotNet_AllBlocks_BuildAndRun()
        {
            var runDir = Build(backend: "dotnet");
            AssertBlocks(RunDotnet, runDir);
            AssertFunctionsEntry(RunDotnet, runDir);
        }

        [Fact]
        public void Tutorial_NetFx_AllBlocks_BuildAndDirectRun()
        {
            var runDir = BuildNetFx("net40");
            AssertBlocks(RunNetFx, runDir);
            AssertFunctionsEntry(RunNetFx, runDir);
        }
    }
}