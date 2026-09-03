using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeGen.Native;
using Cocoa.CodeGen.PE;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native
{
    /// <summary>
    /// 数值类型全集冒烟（6e-M21 Phase 6）：i8/i16/u16/u32/u64/f32 的算术、无符号语义、
    /// 移位符号性、类型转换与单精度浮点在 native x64/x86 双平台的端到端验证。
    /// </summary>
    public class NumericTypesSmokeTests
    {
        private static string GetExePath(string name, TargetPlatform platform)
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-numeric-smoke");
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

        public static IEnumerable<object[]> GetPlatforms()
        {
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X64) };
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X86) };
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Compound_Assignment_And_Typed_Arrays(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    var x: i64 = 100
    x += 50
    x -= 30
    Console.WriteLine(x)
    var f: f32 = 1.5f
    f *= 2.0f
    Console.WriteLine(f64(f))
    var arr: i16[] = new i16[] { 10, 20, 30 }
    arr[1] = (i16)320
    Console.WriteLine(i32(arr[1]) + i32(arr[0]))
    Console.WriteLine(arr.Length)
    var bytes: u8[] = new u8[3]
    bytes[0] = (u8)200
    Console.WriteLine(i32(bytes[0]))
    Console.WriteLine(bytes.Length)
}", "numeric-compound", (TargetPlatform)platform);
            Assert.Equal(
                "120\n3\n330\n3\n200\n3\n",
                stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Signed_Narrow_Integers_Semantics(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    var s: i8 = (i8)-100
    Console.WriteLine(i32(s))
    Console.WriteLine(i32(s * 2))
    Console.WriteLine(i32((i8)127))
    Console.WriteLine(i32((i8)300))
    var h: i16 = 300
    Console.WriteLine(i32(h * h))
    Console.WriteLine(i32((i16)-40000))
}", "numeric-signed", (TargetPlatform)platform);
            Assert.Equal(
                "-100\n-200\n127\n44\n90000\n25536\n",
                stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Unsigned_Integers_Division_Compare_Shift(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    var w: u16 = 60000
    Console.WriteLine(i64(w + w))
    var m: u32 = 4000000000U
    Console.WriteLine(i64(m / 2U))
    Console.WriteLine(i64(m % 7U))
    Console.WriteLine(m > 3999999999U)
    Console.WriteLine(m < 10U)
    var un: u32 = 0x80000000U
    Console.WriteLine(i64(un >> 1))
    var sg: i32 = -8
    Console.WriteLine(sg >> 1)
    Console.WriteLine(i64(un / 3U))
}", "numeric-unsigned", (TargetPlatform)platform);
            Assert.Equal(
                "120000\n2000000000\n3\nTrue\nFalse\n1073741824\n-4\n715827882\n",
                stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Unsigned_Long_Arithmetic_And_Conversions(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    var big: u64 = 18000000000UL
    Console.WriteLine(i64(big))
    Console.WriteLine(i64(big / 3UL))
    Console.WriteLine(i64(big % 7UL))
    Console.WriteLine(big > 1UL)
    var back: u64 = (u64)big
    Console.WriteLine(i64(back))
    Console.WriteLine(i32(2000000000U + 500000000U))
}", "numeric-u64", (TargetPlatform)platform);
            Assert.Equal(
                "18000000000\n6000000000\n3\nTrue\n18000000000\n-1794967296\n",
                stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Float32_Single_Precision_Sse(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    var a: f32 = 1.5f
    var b: f32 = 0.25f
    Console.WriteLine(f64(a + b))
    Console.WriteLine(f64(a * b))
    Console.WriteLine(f64(a / b))
    Console.WriteLine(f64(-a))
    Console.WriteLine(a > b)
    Console.WriteLine(a == 1.5f)
    Console.WriteLine(i32(3.9f))
    Console.WriteLine(f64(f32(2.75)))
    Console.WriteLine(i64(a * 4.0f))
}", "numeric-f32", (TargetPlatform)platform);
            Assert.Equal(
                "1.75\n0.375\n6\n-1.5\nTrue\nTrue\n3\n2.75\n6\n",
                stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Mixed_Width_Conversions_Matrix(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    Console.WriteLine(i64(60000U))
    Console.WriteLine(i64(60000))
    Console.WriteLine(i32((u16)70000))
    Console.WriteLine(i32((i16)70000))
    Console.WriteLine(i64((u8)-1))
    Console.WriteLine(i32((i8)-1))
    Console.WriteLine(f64(f32(1)))
    Console.WriteLine(i64(2.5f))
}", "numeric-mixed", (TargetPlatform)platform);
            Assert.Equal(
                "60000\n60000\n4464\n4464\n255\n-1\n1\n2\n",
                stdout);
        }
    }
}
