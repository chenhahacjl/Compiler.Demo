using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Cocoa.CodeAnalysis.Emit.IL;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    public class AppHostTests
    {
        private static string GetCocDllPath()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "cocoa.dll");
            Assert.True(File.Exists(path), $"CLI assembly not found at '{path}'. Build Cocoa.Compiler first.");
            return path;
        }

        private static string GetTempDir()
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-apphost-tests");
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static string NewRunDir(string run)
        {
            return Path.Combine(GetTempDir(), run, Guid.NewGuid().ToString("N"));
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

        private static (int ExitCode, string Stdout, string Stderr) RunExe(string exePath, params string[] arguments)
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
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            return (process.ExitCode, stdout, stderr);
        }

        [Fact]
        public void FindDefaultTemplate_LocatesSdkAppHost()
        {
            var template = AppHostPatcher.FindDefaultTemplate();
            Assert.True(File.Exists(template), $"apphost template not found: {template}");
            Assert.EndsWith(Path.Combine("AppHostTemplate", "apphost.exe"), template);
        }

        [Fact]
        public void Patch_ReplacesPlaceholderAndZeroPads()
        {
            var root = NewRunDir("patch-bytes");
            Directory.CreateDirectory(root);
            var template = AppHostPatcher.FindDefaultTemplate();
            var output = Path.Combine(root, "App.exe");

            AppHostPatcher.Patch(template, output, "sub/App.dll");

            var templateBytes = File.ReadAllBytes(template);
            var outputBytes = File.ReadAllBytes(output);
            Assert.Equal(templateBytes.Length, outputBytes.Length);

            var placeholder = Encoding.ASCII.GetBytes("c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2");
            Assert.Equal(-1, IndexOf(outputBytes, placeholder));

            var pathBytes = Encoding.UTF8.GetBytes("sub/App.dll");
            var position = IndexOf(outputBytes, pathBytes);
            Assert.True(position >= 0, "patched apphost must contain the app binary path");

            // 补零：路径后紧跟 NUL，且占位符区（≤62B）其余为 0
            Assert.Equal(0, outputBytes[position + pathBytes.Length]);
            for (var i = pathBytes.Length; i < placeholder.Length; i++)
            {
                Assert.Equal(0, outputBytes[position + i]);
            }

            // 模板源文件不被改动
            Assert.Equal(templateBytes, File.ReadAllBytes(template));
        }

        [Fact]
        public void Patch_TooLongPath_Throws()
        {
            var root = NewRunDir("patch-too-long");
            Directory.CreateDirectory(root);
            var template = AppHostPatcher.FindDefaultTemplate();
            var output = Path.Combine(root, "App.exe");
            var longPath = new string('a', AppHostPatcher.MaxAppBinaryPathSizeInBytes + 1) + ".dll";

            Assert.Throws<ArgumentException>(() => AppHostPatcher.Patch(template, output, longPath));
        }

        [Fact]
        public void Patch_MissingPlaceholder_Throws()
        {
            var root = NewRunDir("patch-missing");
            Directory.CreateDirectory(root);
            var bogusTemplate = Path.Combine(root, "bogus.exe");
            File.WriteAllText(bogusTemplate, "this is not an apphost template");
            var output = Path.Combine(root, "App.exe");

            Assert.Throws<NotSupportedException>(() => AppHostPatcher.Patch(bogusTemplate, output, "App.dll"));
        }

        [Fact]
        public void Build_NetCore_ProducesApphostLayout()
        {
            var projectDir = CreateProject("netcore-layout", "Hello", "function Main() { print(\"hello apphost\") }");
            var (exitCode, stdout, stderr) = RunCli($"build \"{projectDir}\" -b dotnet --dotnet-runtime net9.0");
            Assert.True(exitCode == 0, $"build failed ({exitCode}). stdout=[{stdout}] stderr=[{stderr}]");

            var root = Path.GetDirectoryName(projectDir)!;
            var exePath = Path.Combine(root, "Hello.exe");
            var dllPath = Path.Combine(root, "Hello.dll");
            Assert.True(File.Exists(exePath), "native apphost exe missing");
            Assert.True(File.Exists(dllPath), "managed dll missing");
            Assert.True(File.Exists(Path.Combine(root, "Hello.runtimeconfig.json")), "runtimeconfig missing");

            // exe 是原生 apphost（与 SDK 模板等长），不是托管程序集
            var template = AppHostPatcher.FindDefaultTemplate();
            Assert.Equal(File.ReadAllBytes(template).Length, File.ReadAllBytes(exePath).Length);

            // 直接运行（双击等价）：stdout 正确、退出码 0、无宿主 stderr
            var run = RunExe(exePath);
            Assert.Equal(0, run.ExitCode);
            Assert.Contains("hello apphost", run.Stdout);
            Assert.Equal("", run.Stderr);

            // `dotnet Hello.dll` 回归仍可运行
            var viaDotnet = RunExe("dotnet", dllPath);
            Assert.Equal(0, viaDotnet.ExitCode);
            Assert.Contains("hello apphost", viaDotnet.Stdout);
        }

        [Fact]
        public void Build_NetCore_ArgsPassthrough()
        {
            var projectDir = CreateProject("netcore-args", "ArgsApp", "function Main(args: string[]) { print(args.Length) print(args[0]) print(args[1]) }");
            var (exitCode, stdout, stderr) = RunCli($"build \"{projectDir}\" -b dotnet --dotnet-runtime net9.0");
            Assert.True(exitCode == 0, $"build failed ({exitCode}). stdout=[{stdout}] stderr=[{stderr}]");

            var exePath = Path.Combine(Path.GetDirectoryName(projectDir)!, "ArgsApp.exe");
            var run = RunExe(exePath, "alpha", "beta");
            Assert.Equal(0, run.ExitCode);
            Assert.Contains("2", run.Stdout);
            Assert.Contains("alpha", run.Stdout);
            Assert.Contains("beta", run.Stdout);
        }

        private static string CreateProject(string run, string projectName, string source)
        {
            var directory = Path.Combine(NewRunDir(run), projectName);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, projectName + ".co"), source);
            File.WriteAllText(Path.Combine(directory, projectName + ".coproj"), $@"
name = {projectName}
output = executable
platform = x64
entry = Main

[sources]
*.co
");
            return Path.Combine(directory, projectName + ".coproj");
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length)
            {
                return -1;
            }

            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
