using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.Targeting;
using Cocoa.CodeGen.Native;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Tests.CodeAnalysis.Emit.Native;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 閲嶈浇涓夊悗绔攣瀹氾細鏋勯€犲櫒閲嶈浇锛堝０鏄庣绛惧悕鏌ラ噸 + 璋冪敤绔寜鍙傛暟绫诲瀷閫夋嫨锛変笌椤跺眰鍑芥暟閲嶈浇
    /// 锛圔oundScope 绛惧悕鏀捐 + native FunctionIrName 椤跺眰 mangle锛夈€?    /// </summary>
    public class OverloadThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Template = @"using System

public class Counter
{
    private _v: i32

    public constructor(seed: i32)
    {
        _v = seed
    }

    public constructor(seed: i32, incr: i32)
    {
        _v = seed + incr
    }

    public function Value(): i32
    {
        return _v
    }
}

function Square(n: i32): i32
{
    return n * n
}

function Square(s: string): i32
{
    return s.Length
}

function Main(): i32
{
    var a = new Counter(3)
    var b = new Counter(3, 4)
    System.Console.WriteLine(a.Value() == 3)
    System.Console.WriteLine(b.Value() == 7)
    System.Console.WriteLine(Square(5) == 25)
    System.Console.WriteLine(Square(""abcd"") == 4)
    return 0
}";

        private const string Expected = "True\nTrue\nTrue\nTrue\n";

        [Fact]
        public void Evaluator_Overload()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
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
        public void IlE2e_Overload()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-ovl", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "ovl.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.Emit("ovltest", References(), exePath, IlTarget.Parse("net9.0"));
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
        public void NativeE2e_Overload(string target)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-ovl", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "ovl-" + target + ".exe");
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.EmitNative("ovlsdk", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(Expected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
        }
    }
}


