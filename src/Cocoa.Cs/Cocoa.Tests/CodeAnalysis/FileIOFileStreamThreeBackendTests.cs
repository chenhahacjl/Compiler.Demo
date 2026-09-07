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
    /// System.IO.FileStream（9c 起普通 class + _h + 真 seek）三后端锁定。
    /// Evaluator/IL/native 统一走 IOSyscall 本体：Read/Length/Position(se/get)/Write/Flush/Close/Dispose。
    /// </summary>
    public class SystemIOFileStreamThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Template = @"using System
using System.IO

function Main(): i32
{
    var seed = new u8[16]
    var k = 0
    while k < seed.Length
    {
        seed[k] = (u8)(48 + k)
        k = k + 1
    }
    System.IO.File.WriteAllBytes(""{mp}"", seed)
    let fs = new FileStream(""{mp}"")
    let buf = new u8[4]
    var n = fs.Read(buf, 0, 4)
    System.Console.WriteLine(n == 4)
    System.Console.WriteLine(buf[0] == 48)
    System.Console.WriteLine(fs.Length == 16)
    System.Console.WriteLine(fs.Position == 4)
    fs.Position = 8
    let wbuf = new u8[1]
    wbuf[0] = (u8)97
    fs.Write(wbuf, 0, 1)
    fs.Position = 0
    let check = new u8[16]
    var read = fs.Read(check, 0, 16)
    System.Console.WriteLine(read == 16)
    System.Console.WriteLine(check[8] == 97)
    fs.Close()
    return 0
}";

        private const string Expected = "True\nTrue\nTrue\nTrue\nTrue\nTrue\n";

        private const string MultiCtorTemplate = @"using System
using System.IO

function Main(): i32
{
    var w = new u8[5]
    var k = 0
    while k < 5
    {
        w[k] = (u8)(104 + k)
        k = k + 1
    }
    var a = new FileStream(""{mp}"", FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)
    a.Write(w, 0, 5)
    a.Close()
    var b = new FileStream(""{mp}"", FileMode.Truncate, FileAccess.ReadWrite, FileShare.ReadWrite)
    System.Console.WriteLine(b.Length)
    b.Close()
    var c = new FileStream(""{mp}"", FileMode.Append, FileAccess.Write, FileShare.ReadWrite)
    var x = new u8[3]
    x[0] = (u8)120
    x[1] = (u8)121
    x[2] = (u8)122
    c.Write(x, 0, 3)
    c.Close()
    var d = new FileStream(""{mp}"", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)
    System.Console.WriteLine(d.Length)
    var r = new u8[3]
    d.Read(r, 0, 3)
    d.Close()
    System.Console.WriteLine(r[0])
    System.Console.WriteLine(r[1])
    System.Console.WriteLine(r[2])
    return 0
}";

        private const string MultiCtorExpected = "0\n3\n120\n121\n122\n";

        private static string MakePath(string leaf) => Path.Combine(Path.GetTempPath(), "cocoa-fs", leaf);

        [Fact]
        public void Evaluator_FileStream()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
var mp = MakePath(Guid.NewGuid().ToString("N") + ".bin");
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template.Replace("{mp}", mp.Replace('\\', '/'))));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                var actual = writer.ToString().Replace("\r\n", "\n");
                Assert.True(actual == Expected, "GOT:\n" + actual);
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void IlE2e_FileStream()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-fs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
var mp = Path.Combine(dir, "fs.bin");
            var exePath = Path.Combine(dir, "fs.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template.Replace("{mp}", mp.Replace('\\', '/'))));
            var diagnostics = compilation.Emit("fstest", References(), exePath, IlTarget.Parse("net9.0"));
            Assert.Empty(string.Join("\n", diagnostics));
            var psi = new ProcessStartInfo("dotnet", $"\"{exePath}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            using var errReader = process.StandardError;
            var errTask = errReader.ReadToEndAsync();
            Assert.True(process.WaitForExit(20000));
            outputTask.Wait();
            var stdout = Encoding.UTF8.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            Assert.True(process.ExitCode == 0, "exit=" + process.ExitCode + " stderr=" + errTask.Result.Replace("\n", " | "));
            Assert.Equal(Expected, stdout);
        }

                [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_FileStream(string target)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-fs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
var mp = Path.Combine(dir, "fs.bin");
            var exePath = Path.Combine(dir, "fs-" + target + ".exe");
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template.Replace("{mp}", mp.Replace('\\', '/'))));
            var diagnostics = compilation.EmitNative("fssdk", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(Expected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
        }

        [Fact]
        public void Evaluator_FileStreamMultiCtor()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var mp = MakePath(Guid.NewGuid().ToString("N") + ".bin");
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(MultiCtorTemplate.Replace("{mp}", mp.Replace('\\', '/'))));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                var actual = writer.ToString().Replace("\r\n", "\n");
                Assert.True(actual == MultiCtorExpected, "GOT:\n" + actual);
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void IlE2e_FileStreamMultiCtor()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-fs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var mp = Path.Combine(dir, "fs.bin");
            var exePath = Path.Combine(dir, "fs.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(MultiCtorTemplate.Replace("{mp}", mp.Replace('\\', '/'))));
            var diagnostics = compilation.Emit("fstest", References(), exePath, IlTarget.Parse("net9.0"));
            Assert.Empty(string.Join("\n", diagnostics));
            var psi = new ProcessStartInfo("dotnet", $"\"{exePath}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            using var errReader = process.StandardError;
            var errTask = errReader.ReadToEndAsync();
            Assert.True(process.WaitForExit(20000));
            outputTask.Wait();
            var stdout = Encoding.UTF8.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            Assert.True(process.ExitCode == 0, "exit=" + process.ExitCode + " stderr=" + errTask.Result.Replace("\n", " | "));
            Assert.Equal(MultiCtorExpected, stdout);
        }

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_FileStreamMultiCtor(string target)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-fs", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var mp = Path.Combine(dir, "fs.bin");
            var exePath = Path.Combine(dir, "fs-" + target + ".exe");
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(MultiCtorTemplate.Replace("{mp}", mp.Replace('\\', '/'))));
            var diagnostics = compilation.EmitNative("fssdk", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(MultiCtorExpected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
        }
    }
}
