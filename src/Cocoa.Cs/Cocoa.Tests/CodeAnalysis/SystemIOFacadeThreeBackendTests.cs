using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Serialization;
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
    /// System.IO.coa 跨库三后端锁定（2026-09-06）：接口属性（Stream）+ 实例类属性（MemoryStream）随
    /// .coa 序列化（6b 门禁放宽：属性/实例类入库）+ Path 静态容器消费。Evaluator / IL / native ×（x64/x86）。
    /// </summary>
    public class SystemIOFacadeThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Program = @"using System
using System.IO

function Main(): i32
{
    let ms = new MemoryStream()
    let bytes = Runtime.StringToBytes(""abc"")
    ms.Write(bytes, 0, bytes.Length)
    System.Console.WriteLine(ms.Length == 3)
    ms.Position = 1
    let buf = new u8[3]
    let n = ms.Read(buf, 0, 3)
    System.Console.WriteLine(n == 2)
    System.Console.WriteLine(buf[0] == 98)
    System.Console.WriteLine(ms.CanRead && ms.CanSeek)
    let p = Path.Combine(""a"", ""b.txt"")
    System.Console.WriteLine(p)
    System.Console.WriteLine(Path.GetFileName(p) == ""b.txt"")
    System.Console.WriteLine(Path.GetExtension(p) == "".txt"")
    System.Console.WriteLine(Path.GetFileNameWithoutExtension(p) == ""b"")
    return 0
}";

        private const string Expected = "True\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\n";

        private static string ExpectedForCombine => "True\nTrue\nTrue\nTrue\n" + Path.Combine("a", "b.txt") + "\nTrue\nTrue\nTrue\n";

        [Fact]
        public void Evaluator_SystemIO()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                var actual = writer.ToString().Replace("\r\n", "\n");
                Assert.True(actual == ExpectedForCombine, "DIAG:\n" + string.Join("\n", result.Diagnostics.Select(d => d.Message)) + "\nGOT:\n" + actual);
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void IlE2e_SystemIO()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-iofac", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "io.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var diagnostics = compilation.Emit("iotest", References(), exePath, IlTarget.Parse("net9.0"));
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
            Assert.Equal(ExpectedForCombine, stdout);
        }

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_SystemIO(string target)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-iofac", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "io-" + target + ".exe");
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var diagnostics = compilation.EmitNative("iosdk", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(ExpectedForCombine, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
        }
    }
}