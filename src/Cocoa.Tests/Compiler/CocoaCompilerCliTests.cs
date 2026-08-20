using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    public class CocoaCompilerCliTests
    {
        private static string GetCocDllPath()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "cocoa.dll");
            Assert.True(File.Exists(path), $"CLI assembly not found at '{path}'. Build Cocoa.Compiler first.");
            return path;
        }

        private static string GetTempDir()
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-cli-tests");
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static (int ExitCode, string Stdout, string Stderr) Run(string args, string? stdin = null)
        {
            var psi = new ProcessStartInfo("dotnet", $"\"{GetCocDllPath()}\" {args}")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            if (stdin != null)
            {
                process.StandardInput.Write(stdin);
            }

            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            return (process.ExitCode, stdout, stderr);
        }

        [Fact]
        public void Compile_DotNetBackend_Emits_Runnable_Exe()
        {
            var dir = GetTempDir();
            var sourcePath = Path.Combine(dir, "cli-smoke.co");
            var outputPath = Path.Combine(dir, "cli-smoke.exe");
            File.WriteAllText(sourcePath, @"
function twice(x: int): int
{
    return x * 2
}

function Main()
{
    var name = input()
    print(twice(21))
    print(""hello "" + name)
}");

            var (exitCode, stdout, stderr) = Run($"\"{sourcePath}\" -b dotnet -o \"{outputPath}\"");
            Assert.True(exitCode == 0, $"CLI failed with exit {exitCode}: {stderr}");
            Assert.Contains(outputPath, stdout);
            Assert.True(File.Exists(outputPath));

            // 默认 dotnetRuntime = net48（netfx）：产物直接运行，无需 dotnet 前缀
            var psi = new ProcessStartInfo(outputPath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            process.StandardInput.Write("Cocoa");
            process.StandardInput.Close();
            var runStdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("42\r\nhello Cocoa\r\n", runStdout);
        }

        [Fact]
        public void Unknown_Backend_Reports_Error()
        {
            var dir = GetTempDir();
            var sourcePath = Path.Combine(dir, "cli-error.co");
            File.WriteAllText(sourcePath, "function Main() { }");

            var (exitCode, stdout, stderr) = Run($"\"{sourcePath}\" -b foo");
            Assert.Equal(1, exitCode);
            Assert.Contains("unknown backend 'foo'", stderr);
        }

        [Fact]
        public void Help_Prints_Usage()
        {
            var (exitCode, stdout, stderr) = Run("-h");
            Assert.Equal(0, exitCode);
            Assert.Contains("usage: cocoa", stdout);
        }
    }
}