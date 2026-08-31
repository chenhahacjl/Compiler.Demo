using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.IL;
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
    /// P1b：Char.ToString / Boolean.ToString 下沉锁定——原语从 syscall 改为 Runtime.co 里的
    /// 带体 static 方法（纯 Cocoa：new char[1] + elemassign + StringFromChars / 字面量返回），
    /// 三后端（Evaluator/IL/native x64/x86）输出一致。
    /// FacadeMemberTests 只锁 IL，本测试补上 native 覆盖。
    /// </summary>
    public class PrimitiveSinkThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Program = @"using System

function Main(): i32
{
    var c: char = 'A'
    var b: bool = true
    var f: bool = false

    if c.ToString() != ""A"" return 1
    if '0'.ToString().Length != 1 return 2
    if char(65).ToString() != ""A"" return 3
    if b.ToString() != ""True"" return 4
    if f.ToString() != ""False"" return 5

    Console.WriteLine(c.ToString())
    Console.WriteLine(b.ToString())
    Console.WriteLine(f.ToString())
    return 0
}";

        private const string Expected = "A\nTrue\nFalse\n";

        [Fact]
        public void Evaluator_PrimitiveToString_Sunk()
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

        private static string RunIl()
        {
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-primsink", "primsink-il-" + Guid.NewGuid().ToString("N") + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var diagnostics = compilation.Emit("primsink", References(), exePath, IlTarget.Parse("net9.0"));
            Assert.Empty(string.Join("\n", diagnostics));
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo("dotnet", $"\"{exePath}\"") { RedirectStandardOutput = true, UseShellExecute = false };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new TimeoutException("IL exe did not exit in time.");
            }

            outputTask.Wait();
            Assert.Equal(0, process.ExitCode);
            return Encoding.UTF8.GetString(output.ToArray());
        }

        private static string RunNative(string target)
        {
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-primsink", "primsink-native-" + Guid.NewGuid().ToString("N") + "-" + target + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
            var diagnostics = compilation.EmitNative("primsink", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            return NativeEmitTests.Run(exePath);
        }

        [Fact]
        public void IlE2e_PrimitiveToString_Sunk() => Assert.Equal(Expected, RunIl().Replace("\r\n", "\n").Replace("\r", "\n"));

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_PrimitiveToString_Sunk(string target) => Assert.Equal(Expected, RunNative(target).Replace("\r\n", "\n").Replace("\r", "\n"));
    }
}
