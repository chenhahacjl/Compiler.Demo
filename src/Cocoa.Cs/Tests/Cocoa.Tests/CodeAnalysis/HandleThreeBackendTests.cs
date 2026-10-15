using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeGen.Native;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 6e-M35：System.Handle 族（Handle/FileHandle/ProcessHandle + Kernel32.CloseHandle extern）——
    /// 经 System.Core.coa 注入，三后端锁定（base 继承链 + IDisposable + virtual Dispose + using 语句）。
    /// </summary>
    public class HandleThreeBackendTests
    {
        private const string Program = @"using System

function Main(): i32
{
    let h = new Handle(123)
    Console.WriteLine(h.IsNull())
    Console.WriteLine(h.IsNotNull())
    Console.WriteLine(h.ToLong() == 123L)
    Console.WriteLine(h.ToInt() == 123)
    let h2 = new Handle(123)
    Console.WriteLine(h.Equals(h2))
    Console.WriteLine(h.ToString())
    let f = new FileHandle(0)
    Console.WriteLine(f.IsOpen())
    let p = new ProcessHandle(9)
    Console.WriteLine(p.IsValid())
    let r = new Handle(7)
    r.Dispose()
    Console.WriteLine(r.ToInt() == 7)
    Console.WriteLine(""done"")
    return 0
}";

        private const string Expected = "False\nTrue\nTrue\nTrue\nTrue\nHandle(123)\nFalse\nTrue\nTrue\ndone\n";

        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        [Fact]
        public void Evaluator_Handle_Family()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);

                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(Expected, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void Il_E2e_Handle_Family()
        {
            var (exitCode, stdout) = EmitIlAndRun(Program, "handle-il");
            Assert.Equal(0, exitCode);
            Assert.Equal(Expected, stdout.Replace("\r\n", "\n"));
        }

        public static IEnumerable<object[]> GetPlatforms()
        {
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X64) };
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X86) };
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Native_E2e_Handle_Family(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(Program, "handle-native", (TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal(Expected, stdout);
        }

        private static (int ExitCode, string Stdout) EmitIlAndRun(string source, string name)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create("Main", References(), syntaxTree);
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-handle-il");
            Directory.CreateDirectory(directory);
            var exePath = Path.Combine(directory, name + ".exe");
            var diagnostics = compilation.Emit(name, References(), exePath, IlTarget.Parse("net9.0"));

            Assert.Empty(diagnostics);
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo("dotnet", "\"" + exePath + "\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);
            return (process.ExitCode, stdout);
        }

        private static (int ExitCode, string Stdout) EmitNativeAndRun(string source, string name, TargetPlatform platform)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create("Main", Array.Empty<string>(), syntaxTree);
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-handle-native");
            Directory.CreateDirectory(directory);
            var suffix = platform.Arch == Architecture.X86 ? "-x86" : "";
            var exePath = Path.Combine(directory, name + suffix + ".exe");
            var diagnostics = compilation.EmitNative(name, exePath, platform);

            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new TimeoutException("Native exe did not exit in time.");
            }

            outputTask.Wait();
            var stdout = Encoding.Unicode.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            return (process.ExitCode, stdout);
        }
    }
}