using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.Native;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    public class LaunchProcessTests
    {
        private static string[] References() => new[]
        {
            typeof(object).Assembly.Location,
            typeof(System.Console).Assembly.Location,
            typeof(System.IO.File).Assembly.Location,
            typeof(System.Diagnostics.Process).Assembly.Location,
        };

        private const string SimpleProgram = @"using System

function Main(): i32
{
    var exitCode = Runtime.LaunchProcess(""cmd.exe"", ""/c echo hello"")
    Runtime.WriteLine(exitCode)
    return 0
}";

        [Fact]
        public void Il_LaunchProcess_Simple()
        {
            var syntaxTree = SyntaxTree.Parse(SimpleProgram);
            var compilation = Compilation.Create("Main", References(), syntaxTree);
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-launchprocess", "lp-il.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
            var diagnostics = compilation.Emit("lp-il", References(), exePath,
                Cocoa.CodeAnalysis.Emit.IlTarget.Parse("net9.0"));
            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));
        }

        private static (int ExitCode, string Stdout) EmitNativeAndRun(string source, string name)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create("Main", References(), syntaxTree);
            var platform = new TargetPlatform(TargetOS.Windows, Architecture.X64);
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-launchprocess-native");
            Directory.CreateDirectory(directory);
            var exePath = Path.Combine(directory, name + ".exe");
            var diagnostics = compilation.EmitNative(name, exePath, platform);
            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            using var errOutput = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            var errTask = process.StandardError.BaseStream.CopyToAsync(errOutput);
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new TimeoutException("Native exe did not exit in time.");
            }
            outputTask.Wait();
            errTask.Wait();
            var stdout = Encoding.Unicode.GetString(output.ToArray());
            var stderr = Encoding.UTF8.GetString(errOutput.ToArray());
            if (!string.IsNullOrEmpty(stderr))
                stdout += "\n[STDERR] " + stderr;
            return (process.ExitCode, stdout);
        }

        [Fact]
        public void Native_LaunchProcess_Simple()
        {
            var (exitCode, stdout) = EmitNativeAndRun(SimpleProgram, "lp-simple");
            Assert.Equal(0, exitCode);
            Assert.Contains("0", stdout);
        }
    }
}
