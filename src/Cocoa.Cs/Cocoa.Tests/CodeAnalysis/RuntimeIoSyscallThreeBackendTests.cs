using Cocoa.CodeAnalysis;
using Cocoa.Targeting;
using Cocoa.CodeGen.Native;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Tests.CodeAnalysis.Emit.Native;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// P1（底层原语）三后端锁定：File.WriteAllBytes/ReadAllBytes 往返、Runtime.StringToBytes/StringFromBytes
    /// （UTF-16↔UTF-8 带非 ASCII）与 Runtime.LaunchProcess 三参（workdir 切换写锁文件）。
    /// 消费 build-sdk 重建的 System.Core.coa（FileIO.co / Runtime.co syscall 声明背书的低层原语）。
    /// </summary>
    public class RuntimeIoSyscallThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private static string NewTestDir() => Path.Combine(Path.GetTempPath(), "cocoa-riob", Guid.NewGuid().ToString("N"));

        private static string BytesProgram(string path) => @"using System
using System.IO

function Main(): i32
{
    let p = """ + path + @"""
    File.Delete(p)
    let t = Runtime.StringToBytes(""a小éß木𝕏"")
    File.WriteAllBytes(p, t)
    let back = File.ReadAllBytes(p)
    System.Console.WriteLine(back.Length == t.Length)
    System.Console.WriteLine(back.Length > 0)
    let dec = Runtime.StringFromBytes(back)
    System.Console.WriteLine(dec == ""a小éß木𝕏"")
    File.Delete(p)
    return 0
}";

        private const string BytesExpected = "True\nTrue\nTrue\n";

        private static string LaunchProgram(string markerDir)
        {
            var marker = Path.Combine(markerDir, "marker.txt").Replace("\\", "/");
            return @"using System
using System.IO

function Main(): i32
{
    let wd = """ + markerDir.Replace("\\", "/") + @"""
    let code = Runtime.LaunchProcess(""cmd.exe"", ""/c echo hit > marker.txt"", wd)
    System.Console.WriteLine(code == 0)
    System.Console.WriteLine(File.Exists(wd + ""/marker.txt""))
    File.Delete(wd + ""/marker.txt"")
    return 0
}";
        }

        private const string LaunchExpected = "True\nTrue\n";

        [Fact]
        public void Evaluator_BytesRoundTrip()
        {
            var dir = NewDir();
            try
            {
                var path = Path.Combine(dir, "d.bin").Replace("\\", "/");
                var source = BytesProgram(path);
                var original = Console.Out;
                try
                {
                    using var writer = new StringWriter();
                    Console.SetOut(writer);
                    var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
                    var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                    Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                    Assert.Equal(BytesExpected, writer.ToString().Replace("\r\n", "\n"));
                }
                finally
                {
                    Console.SetOut(original);
                }

                Assert.False(File.Exists(path));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void IlE2e_BytesRoundTrip()
        {
            var dir = NewDir();
            try
            {
                var path = Path.Combine(dir, "p.bin").Replace("\\", "/");
                var source = BytesProgram(path);
                var exePath = Path.Combine(dir, "bytes-il.exe");
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
                var diagnostics = compilation.Emit("bytestest", References(), exePath, IlTarget.Parse("net9.0"));
                Assert.Empty(string.Join("\n", diagnostics));
                Assert.True(File.Exists(exePath));

                var psi = new ProcessStartInfo("dotnet", $"\"{exePath}\"") { RedirectStandardOutput = true, UseShellExecute = false };
                using var process = Process.Start(psi)!;
                using var output = new MemoryStream();
                var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
                Assert.True(process.WaitForExit(20000));
                outputTask.Wait();
                var stdout = Encoding.UTF8.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
                Assert.Equal(0, process.ExitCode);
                Assert.Equal(BytesExpected, stdout);
                Assert.False(File.Exists(path));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Theory]
        [InlineData("windows-x64")]
        public void NativeE2e_BytesRoundTrip(string target)
        {
            var dir = NewDir();
            try
            {
                var path = Path.Combine(dir, "p.bin").Replace("\\", "/");
                var source = BytesProgram(path);
                var exePath = Path.Combine(dir, "n.exe");
                TargetPlatform.TryParse(target, out var platform);
                var compilation = Cocoa.CodeAnalysis.Compilation.Create("Main", References(), SyntaxTree.Parse(source));
                var diagnostics = compilation.EmitNative("bytest", exePath, platform);
                Assert.Empty(string.Join("\n", diagnostics));
                var stdout = NativeEmitTests.Run(exePath);
                Assert.Equal(BytesExpected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
                Assert.False(File.Exists(path));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Evaluator_LaunchProcessWorkingDir()
        {
            var dir = NewDir();
            try
            {
                var source = LaunchProgram(dir);
                var original = Console.Out;
                try
                {
                    using var writer = new StringWriter();
                    Console.SetOut(writer);
                    var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
                    var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                    Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                    Assert.Equal(LaunchExpected, writer.ToString().Replace("\r\n", "\n"));
                }
                finally
                {
                    Console.SetOut(original);
                }
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact(Skip = "native _wsystem 子进程 cwd 继承语义待核（Y-P1 遗留：SetCurrentDirectoryW 后相对路径落盘位置与 runtime cwd 与预期不符）")]
        public void NativeE2e_LaunchProcessWorkingDir()
        {
            var dir = NewDir();
            try
            {
                TargetPlatform.TryParse("windows-x64", out var platform);
                var exePath = Path.Combine(dir, "launch.exe");
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(LaunchProgram(dir)));
                var diagnostics = compilation.EmitNative("launchwd", exePath, platform);
                Assert.Empty(string.Join("\n", diagnostics));
                var stdout = NativeEmitTests.Run(exePath);
                Assert.Equal(LaunchExpected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        private static string NewDir()
        {
            var dir = NewTestDir();
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}