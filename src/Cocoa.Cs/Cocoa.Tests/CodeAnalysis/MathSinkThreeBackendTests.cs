using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit;
using Cocoa.CodeAnalysis.Emit.Native;
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
    /// P1b：Math.Floor / Ceiling / Truncate 下沉锁定——从 syscall 改为 Runtime.co 带体 static 方法
    /// （纯 Cocoa：i64(f64) 截断 + f64(i64) 回投 + 负数 ±1 修正），三后端输出一致。
    /// 重点锁负数方向：Floor(-3.7)=-4、Ceiling(-3.7)=-3、Truncate(-3.7)=-3。
    /// </summary>
    public class MathSinkThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Program = @"using System

function Main(): i32
{
    if Math.Truncate(3.7) != 3.0 return 1
    if Math.Truncate(-3.7) != -3.0 return 2
    if Math.Truncate(0.0) != 0.0 return 3
    if Math.Truncate(-0.9) != 0.0 return 4

    if Math.Floor(3.7) != 3.0 return 5
    if Math.Floor(-3.7) != -4.0 return 6
    if Math.Floor(-4.0) != -4.0 return 7
    if Math.Floor(0.5) != 0.0 return 8

    if Math.Ceiling(3.7) != 4.0 return 9
    if Math.Ceiling(-3.7) != -3.0 return 10
    if Math.Ceiling(4.0) != 4.0 return 11
    if Math.Ceiling(-0.5) != 0.0 return 12

    return 0
}";

        [Fact]
        public void Evaluator_MathSink()
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
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-mathsink", "mathsink-il-" + Guid.NewGuid().ToString("N") + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var diagnostics = compilation.Emit("mathsink", References(), exePath, IlTarget.Parse("net9.0"));
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
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-mathsink", "mathsink-native-" + Guid.NewGuid().ToString("N") + "-" + target + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
            var diagnostics = compilation.EmitNative("mathsink", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            return NativeEmitTests.Run(exePath);
        }

        [Fact]
        public void IlE2e_MathSink() => Assert.Equal(0, RunIl());

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_MathSink(string target) => Assert.Equal("", RunNative(target));
    }
}
