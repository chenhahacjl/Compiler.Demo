using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeGen.Native;
using Cocoa.Targeting;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native
{
    public class LongSmokeTests
    {
        private static string GetExePath(string name, TargetPlatform platform)
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-long-smoke");
            Directory.CreateDirectory(directory);
            var suffix = platform.Arch == Architecture.X86 ? "-x86" : "";
            return Path.Combine(directory, name + suffix + ".exe");
        }

        private static (int ExitCode, string Stdout) EmitNativeAndRun(string source, string name, TargetPlatform platform)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var exePath = GetExePath(name, platform);
            var diagnostics = compilation.EmitNative(name, exePath, platform);

            if (!diagnostics.IsEmpty)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "cocoa-long-diag.txt"),
                    "count=" + diagnostics.Length + "\n" + string.Join("\n", System.Linq.Enumerable.Select(diagnostics, d => d.Message)));
            }

            Assert.True(diagnostics.IsEmpty, "see cocoa-long-diag.txt");
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

        public static IEnumerable<object[]> GetPlatforms()
        {
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X64) };
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X86) };
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Long_Arithmetic_ToString(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    var a = 1000000L;
    var b = 7L;
    Console.WriteLine(a * b);
    Console.WriteLine(a / b);
    Console.WriteLine(a % b);
    Console.WriteLine(a + b);
    Console.WriteLine(a - b);
    Console.WriteLine(a - 1000000L);
    Console.WriteLine(a * a);
    var c = -12345L;
    Console.WriteLine(c);
    Console.WriteLine(c * -2L);
    var big = 9223372036854775807L;
    Console.WriteLine(big);
}", "long_arith", (TargetPlatform)platform);
            Assert.Equal(
                "7000000\n142857\n1\n1000007\n999993\n0\n1000000000000\n-12345\n24690\n9223372036854775807\n",
                stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Long_Compare(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    var a = 5L;
    var b = 10L;
    Console.WriteLine(a < b);
    Console.WriteLine(a > b);
    Console.WriteLine(a == b);
    Console.WriteLine(a != b);
    Console.WriteLine(a <= b);
    Console.WriteLine(a >= b);
    Console.WriteLine(a == 5L);
    Console.WriteLine(a >= 0L);
    Console.WriteLine(-5L < 3L);
}", "long_cmp", (TargetPlatform)platform);
            Assert.Equal(
                "True\nFalse\nFalse\nTrue\nTrue\nFalse\nTrue\nTrue\nTrue\n",
                stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Long_Parse(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    var max = 9223372036854775807L;
    Console.WriteLine(max);
    var zero = 0L;
    Console.WriteLine(zero);
    var neg = -1L;
    Console.WriteLine(neg);
    var mid = 2147483647L;
    Console.WriteLine(mid);
    var big = 123456789012345L;
    Console.WriteLine(big);
    var hi = -9223372036854775807L;
    Console.WriteLine(hi);
}", "long_parse", (TargetPlatform)platform);
            Assert.Equal(
                "9223372036854775807\n0\n-1\n2147483647\n123456789012345\n-9223372036854775807\n",
                stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Long_Bitwise_Shift_And_Conversions(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    var x = 0xFL
    var y = 0x3L
    Console.WriteLine(x & y)
    Console.WriteLine(x | y)
    Console.WriteLine(x ^ y)
    Console.WriteLine(~x)
    var s = 1L
    Console.WriteLine(s << 4)
    Console.WriteLine(s >> 2)
    var i = 5
    var l = 10L
    Console.WriteLine(i + l)
    Console.WriteLine(l - i)
    Console.WriteLine(i * l)
    var big = 123456789012L
    Console.WriteLine((i32)big)
    Console.WriteLine((f64)big)
    Console.WriteLine((i64)(f64)big)
    var d = 123456789012.0
    Console.WriteLine((i64)d)
    Console.WriteLine((i64)3.9)
    Console.WriteLine((i64)-2.9)
    Console.WriteLine((i64)i)
    Console.WriteLine(-big)
    Console.WriteLine(+big)
    Console.WriteLine(i == l)
    Console.WriteLine(i < l)
}", "long_bitwise", (TargetPlatform)platform);
            Assert.Equal(
                "3\n15\n12\n-16\n16\n0\n15\n5\n50\n-1097262572\n123456789012\n123456789012\n123456789012\n3\n-2\n5\n-123456789012\n123456789012\nFalse\nTrue\n",
                stdout);
        }
    }
}
