using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>进程内调用 `cocoa.dll` 的子进程 runner（复用现有 CLI 测试模式）。</summary>
    internal static class CliTestRunner
    {
        public static string GetCocoaDllPath()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "cocoa.dll");
            Assert.True(File.Exists(path), $"CLI assembly not found at '{path}'. Build Cocoa.Compiler first.");
            return path;
        }

        public static (int ExitCode, string Stdout, string Stderr) Run(string args, string workingDirectory)
        {
            var psi = new ProcessStartInfo("dotnet", $"\"{GetCocoaDllPath()}\" {args}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory,
            };
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            return (process.ExitCode, stdout, stderr);
        }

        public static string NewTempDir(string name)
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-cli-" + name + "-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }
    }
}
