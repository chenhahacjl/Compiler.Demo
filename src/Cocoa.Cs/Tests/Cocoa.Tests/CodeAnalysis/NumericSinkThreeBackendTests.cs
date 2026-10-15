using Cocoa.CodeAnalysis;
using Cocoa.Targeting;
using Cocoa.CodeGen.Native;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Tests.CodeAnalysis.Emit.Native;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// P1c：Int32ToString / Int64ToString / UInt64ToString / ParseInt64 下沉锁定——
    /// 从 syscall 改为 Runtime.co 带体 static 方法（纯 Cocoa：char 缓冲反向填充 +
    /// StringFromChars；Parse 用 u64 累加容纳 |i64.MinValue|），三后端输出一致。
    /// 重点锁边界：i32.MinValue、i64.MinValue、u64.MaxValue，
    /// 以及 90fa7df 修过的 ParseInt64 native 负号缺失类缺陷。
    /// </summary>
    public class NumericSinkThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Program = @"using System

function Main(): i32
{
    var a: i32 = 0
    if a.ToString() != ""0"" return 1
    var b: i32 = 42
    if b.ToString() != ""42"" return 2
    var c: i32 = -42
    if c.ToString() != ""-42"" return 3
    var d: i32 = 1000
    if d.ToString() != ""1000"" return 4
    var e: i32 = 2147483647
    if e.ToString() != ""2147483647"" return 5
    var f: i32 = -2147483647 - 1
    if f.ToString() != ""-2147483648"" return 6

    var g: i64 = 0
    if g.ToString() != ""0"" return 7
    var h: i64 = 1234567890123l
    if h.ToString() != ""1234567890123"" return 8
    var k: i64 = 9223372036854775807l
    if k.ToString() != ""9223372036854775807"" return 9
    var m: i64 = -9223372036854775807l - 1l
    if m.ToString() != ""-9223372036854775808"" return 10

    var p: u64 = 0ul
    if p.ToString() != ""0"" return 11
    var q: u64 = 12345678901234567890ul
    if q.ToString() != ""12345678901234567890"" return 12
    var r: u64 = 18446744073709551615ul
    if r.ToString() != ""18446744073709551615"" return 13

    if Int64.Parse(""12345"") != 12345l return 14
    if Int64.Parse(""-12345"") != -12345l return 15
    if Int64.Parse(""+12345"") != 12345l return 16
    if Int64.Parse(""0"") != 0l return 17
    if Int64.Parse(""-9223372036854775808"") != -9223372036854775807l - 1l return 18

    return 0
}";

        [Fact]
        public void Evaluator_NumericSink()
        {
            using var writer = new StringWriter();
            var original = Console.Out;
            try
            {
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(0, result.Value as int? ?? -999);
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        private static int RunIl()
        {
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-numbersink", "numbersink-il-" + Guid.NewGuid().ToString("N") + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var diagnostics = compilation.Emit("numbersink", References(), exePath, IlTarget.Parse("net9.0"));
            Assert.Empty(string.Join("\n", diagnostics));

            var psi = new ProcessStartInfo("dotnet", $"\"{exePath}\"") { RedirectStandardOutput = true, UseShellExecute = false };
            using var process = Process.Start(psi)!;
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new TimeoutException("IL exe did not exit in time.");
            }

            return process.ExitCode;
        }

        private static string RunNative(string target)
        {
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-numbersink", "numbersink-native-" + Guid.NewGuid().ToString("N") + "-" + target + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
            var diagnostics = compilation.EmitNative("numbersink", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            return NativeEmitTests.Run(exePath);
        }

        [Fact]
        public void IlE2e_NumericSink() => Assert.Equal(0, RunIl());

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_NumericSink(string target) => Assert.Equal("", RunNative(target));
    }
}
