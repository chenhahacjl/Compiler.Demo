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
    /// Native 路径 double 消费 e2e（阶段 6a-5）：字面量/算术/比较/转换/print/double[]，
    /// 与 IL 后端输出对齐；另覆盖定点 6 位舍入剪零与特殊值（x64 与 x86 双平台）。
    /// </summary>
    public class NativeDoubleE2eTests
    {
        private static string GetExePath(string name, TargetPlatform platform)
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-native-double-tests");
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

            Assert.Empty(diagnostics);
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
            var stdout = Encoding.Unicode.GetString(output.ToArray());
            return (process.ExitCode, stdout);
        }

        public static IEnumerable<object[]> GetPlatforms()
        {
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X64) };
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X86) };
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Double_EndToEnd(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    var d: f64 = 3.14
    Console.WriteLine(d)
    Console.WriteLine(1.5 + 2.25)
    Console.WriteLine(10.0 / 4)
    Console.WriteLine(2.5 * 2)
    Console.WriteLine(7 - 1.5)
    Console.WriteLine(1.5 < 2.5)
    Console.WriteLine(1.5 == 1.5)
    Console.WriteLine((i32)3.9)
    Console.WriteLine((u8)3.9)
    Console.WriteLine((f64)3)
    var arr: f64[] = new f64[2] {1.5, 2.5}
    arr[0] = 3.5
    Console.WriteLine(arr[0])
    Console.WriteLine(arr[1])
    var sum: f64 = 0.0
    sum = sum + arr[0]
    Console.WriteLine(sum)
}", "e2e-double", (TargetPlatform)platform);

            Assert.Equal(0, exitCode);
            Assert.Equal("3.14\r\n3.75\r\n2.5\r\n5\r\n5.5\r\nTrue\r\nTrue\r\n3\r\n3\r\n3\r\n3.5\r\n2.5\r\n3.5\r\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Double_FixedPointFormatting_AndSpecialValues(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    Console.WriteLine(0.0)
    Console.WriteLine(-1.5)
    Console.WriteLine(1.0 / 3.0)
    Console.WriteLine(2.0 / 3.0)
    Console.WriteLine(100.0 / 7)
    Console.WriteLine(123456789.0)
    Console.WriteLine(1.0 / 0.0)
    Console.WriteLine(0.0 / 0.0)
    Console.WriteLine(""d="" + 1.5)
    Console.WriteLine((string)2.75)
    var x: f64 = -0.0
    Console.WriteLine(x)
    Console.WriteLine(0.1 + 0.2)
}", "e2e-double-fmt", (TargetPlatform)platform);

            Assert.Equal(0, exitCode);
            Assert.Equal("0\r\n-1.5\r\n0.333333\r\n0.666667\r\n14.285714\r\n123456789\r\nInfinity\r\nNaN\r\nd=1.5\r\n2.75\r\n-0\r\n0.3\r\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Format_Codes_Alignment_And_Types(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    Console.WriteLine($""{255:D}"")
    Console.WriteLine($""{255:X}"")
    Console.WriteLine($""{255:x}"")
    Console.WriteLine($""{42,5}"")
    Console.WriteLine($""{-42,-6}"")
    Console.WriteLine($""{true}"")
    Console.WriteLine($""{false}"")
    Console.WriteLine($""{'A'}"")
    Console.WriteLine($""{'Z',3}"")
    Console.WriteLine($""{3.14159:F2}"")
    Console.WriteLine($""{2.5:F0}"")
}", "e2e-format-codes", (TargetPlatform)platform);

            Assert.Equal("255\r\nFF\r\nff\r\n   42\r\n-42   \r\nTrue\r\nFalse\r\nA\r\n  Z\r\n3.14\r\n3\r\n", stdout);
            Assert.Equal(0, exitCode);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Format_Codes_Double_F_Precision_Rounding_Alignment_And_Specials(object platform)
        {
            // 自定义 F 语义：round-half-away-from-zero（与旧 FormatInt 一致）；F 无显式精度默认 2 位。
            // 注意：DoubleFixed 用 int32 承载 value×10^n，|value×10^n| > 2^31 时截断（等价于 DoubleToString 的 2^55 高位截断限制）。
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    Console.WriteLine($""{2.5:F}"")
    Console.WriteLine($""{3.14159:F}"")
    Console.WriteLine($""{3.14159:F0}"")
    Console.WriteLine($""{3.14159:F1}"")
    Console.WriteLine($""{3.14159:F3}"")
    Console.WriteLine($""{2.5:F1}"")
    Console.WriteLine($""{2.5:F2}"")
    Console.WriteLine($""{2.5:F3}"")
    Console.WriteLine($""{2.5:f2}"")
    Console.WriteLine($""{0.5:F0}"")
    Console.WriteLine($""{1.5:F0}"")
    Console.WriteLine($""{2.5:F0}"")
    Console.WriteLine($""{-0.5:F0}"")
    Console.WriteLine($""{-2.5:F0}"")
    Console.WriteLine($""{-3.5:F0}"")
    Console.WriteLine($""{-2.55:F1}"")
    Console.WriteLine($""{123456.789:F0}"")
    Console.WriteLine($""{1234567.89:F1}"")
    Console.WriteLine($""{3.14159,10:F2}"")
    Console.WriteLine($""{3.14159,-10:F2}"")
    Console.WriteLine($""{1.5,6:F1}"")
    Console.WriteLine($""{1.0/0.0:F2}"")
    Console.WriteLine($""{0.0/0.0:F2}"")
    Console.WriteLine($""{0.0:F1}"")
    Console.WriteLine($""{-0.0:F2}"")
}", "e2e-format-f", (TargetPlatform)platform);

            Assert.Equal(
                "2.50\r\n" +
                "3.14\r\n" +
                "3\r\n" +
                "3.1\r\n" +
                "3.142\r\n" +
                "2.5\r\n" +
                "2.50\r\n" +
                "2.500\r\n" +
                "2.50\r\n" +
                "1\r\n" +
                "2\r\n" +
                "3\r\n" +
                "-1\r\n" +
                "-3\r\n" +
                "-4\r\n" +
                "-2.5\r\n" +
                "123457\r\n" +
                "1234567.9\r\n" +
                "      3.14\r\n" +
                "3.14      \r\n" +
                "   1.5\r\n" +
                "Infinity\r\n" +
                "NaN\r\n" +
                "0.0\r\n" +
                "0.00\r\n", stdout);
            Assert.Equal(0, exitCode);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Format_Codes_Double_F_FullRange(object platform)
        {
            // 全 double 范围定点 F：1280 位大整数定点 value×10^n，round-half-away-from-zero。
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    Console.WriteLine($""{123456789.0:F2}"")
    Console.WriteLine($""{2147483647.0:F0}"")
    Console.WriteLine($""{999999999.99:F2}"")
    Console.WriteLine($""{123456789012345678.0:F0}"")
    Console.WriteLine($""{123456789012345678.0:F2}"")
    Console.WriteLine($""{1e22:F1}"")
    Console.WriteLine($""{1.5:F20}"")
    Console.WriteLine($""{1234567.89:F1}"")
    Console.WriteLine($""{0.0:F5}"")
    Console.WriteLine($""{-0.0:F2}"")
}", "e2e-format-f-fullrange", (TargetPlatform)platform);

            Assert.Equal(
                "123456789.00\r\n" +
                "2147483647\r\n" +
                "999999999.99\r\n" +
                "123456789012345680\r\n" +
                "123456789012345680.00\r\n" +
                "10000000000000000000000.0\r\n" +
                "1.50000000000000000000\r\n" +
                "1234567.9\r\n" +
                "0.00000\r\n" +
                "0.00\r\n", stdout);
            Assert.Equal(0, exitCode);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Format_Codes_Double_E_Scientific(object platform)
        {
            // E 格式（.NET 语义）：尾数 round-half-away-from-zero，指数 3 位补零。
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    Console.WriteLine($""{12345.678:E2}"")
    Console.WriteLine($""{0.001:E1}"")
    Console.WriteLine($""{12345.678:E0}"")
    Console.WriteLine($""{2.5:E0}"")
    Console.WriteLine($""{1.5:E0}"")
    Console.WriteLine($""{9.99:E1}"")
    Console.WriteLine($""{0.0:E2}"")
    Console.WriteLine($""{-0.0:E2}"")
    Console.WriteLine($""{1e22:E2}"")
    Console.WriteLine($""{0.00000000000000000001:E2}"")
    Console.WriteLine($""{999999.999:E2}"")
    Console.WriteLine($""{-12345.678:E2}"")
    Console.WriteLine($""{12345.678:e2}"")
    Console.WriteLine($""{12345.678:g3}"")
    Console.WriteLine($""{1.5e-3:E2}"")
    Console.WriteLine($""{0.5:E2}"")
    Console.WriteLine($""{1.0:E}"")
    Console.WriteLine($""{12345.678:E}"")
    Console.WriteLine($""{999.9:E}"")
    Console.WriteLine($""{1.0/0.0:E2}"")
    Console.WriteLine($""{0.0/0.0:E2}"")
}", "e2e-format-e", (TargetPlatform)platform);

            Assert.Equal(
                "1.23E+004\r\n" +
                "1.0E-003\r\n" +
                "1E+004\r\n" +
                "3E+000\r\n" +
                "2E+000\r\n" +
                "1.0E+001\r\n" +
                "0.00E+000\r\n" +
                "0.00E+000\r\n" +
                "1.00E+022\r\n" +
                "1.00E-020\r\n" +
                "1.00E+006\r\n" +
                "-1.23E+004\r\n" +
                "1.23e+004\r\n" +
                "1.23e+04\r\n" +
                "1.50E-003\r\n" +
                "5.00E-001\r\n" +
                "1.000000E+000\r\n" +
                "1.234568E+004\r\n" +
                "9.999000E+002\r\n" +
                "Infinity\r\n" +
                "NaN\r\n", stdout);
            Assert.Equal(0, exitCode);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Format_Codes_Double_G_General(object platform)
        {
            // G 格式（.NET 语义）：-4 <= e < p 定点否则科学；剪尾零去尾点；指数 2 位补零；round-half-away-from-zero。
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    Console.WriteLine($""{123456.789:G3}"")
    Console.WriteLine($""{0.00001234:G2}"")
    Console.WriteLine($""{0.001234:G2}"")
    Console.WriteLine($""{0.001234:G3}"")
    Console.WriteLine($""{123.4:G5}"")
    Console.WriteLine($""{100.0:G3}"")
    Console.WriteLine($""{100.0:G2}"")
    Console.WriteLine($""{2.5:G2}"")
    Console.WriteLine($""{0.0:G3}"")
    Console.WriteLine($""{0.0001:G2}"")
    Console.WriteLine($""{0.00001:G2}"")
    Console.WriteLine($""{999999.999:G3}"")
    Console.WriteLine($""{123456789012345678.0:G4}"")
    Console.WriteLine($""{0.05:G2}"")
    Console.WriteLine($""{1.0:G}"")
    Console.WriteLine($""{123456789.0:G}"")
    Console.WriteLine($""{1e22:G}"")
    Console.WriteLine($""{1e15:G}"")
    Console.WriteLine($""{1e5:G}"")
    Console.WriteLine($""{0.0001:G}"")
}", "e2e-format-g", (TargetPlatform)platform);

            Assert.Equal(
                "1.23E+05\r\n" +
                "1.2E-05\r\n" +
                "0.0012\r\n" +
                "0.00123\r\n" +
                "123.4\r\n" +
                "100\r\n" +
                "1E+02\r\n" +
                "2.5\r\n" +
                "0\r\n" +
                "0.0001\r\n" +
                "1E-05\r\n" +
                "1E+06\r\n" +
                "1.235E+17\r\n" +
                "0.05\r\n" +
                "1\r\n" +
                "123456789\r\n" +
                "1E+22\r\n" +
                "1E+15\r\n" +
                "100000\r\n" +
                "0.0001\r\n", stdout);
            Assert.Equal(0, exitCode);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Format_Codes_Double_Boundaries(object platform)
        {
            // 全 double 范围边界：max double / 最小亚正规 / 最小正规，e-notation 字面量。
            // 注：MaxValue:F2 为精确大整数定点（309 位整数）——native 优于 .NET F2（内部走 Decimal 截断到 ~29 位有效数字）。
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    Console.WriteLine($""{1.7976931348623157E+308:E2}"")
    Console.WriteLine($""{1.7976931348623157E+308:G}"")
    Console.WriteLine($""{1.7976931348623157E+308:F2}"")
    Console.WriteLine($""{5E-324:E2}"")
    Console.WriteLine($""{5E-324:G}"")
    Console.WriteLine($""{5E-324:F2}"")
    Console.WriteLine($""{1E-308:G}"")
    Console.WriteLine($""{1E-308:E2}"")
    Console.WriteLine($""{1E-308:F5}"")
}", "e2e-format-boundaries", (TargetPlatform)platform);

            Assert.Equal(
                "1.80E+308\r\n" +
                "1.79769313486232E+308\r\n" +
                "179769313486231570814527423731704356798070567525844996598917476803157260780028538760589558632766878171540458953514382464234321326889464182768467546703537516986049910576551282076245490090389328944075868508455133942304583236903222948165808559332123348274797826204144723168738177180919299881250404026184124858368.00\r\n" +
                "4.94E-324\r\n" +
                "4.94065645841247E-324\r\n" +
                "0.00\r\n" +
                "1E-308\r\n" +
                "1.00E-308\r\n" +
                "0.00000\r\n", stdout);
            Assert.Equal(0, exitCode);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Math_Primitives_EndToEnd(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    Console.WriteLine(Math.Sqrt(2.0))
    Console.WriteLine(Math.Floor(2.7))
    Console.WriteLine(Math.Floor(-2.7))
    Console.WriteLine(Math.Ceiling(2.1))
    Console.WriteLine(Math.Ceiling(-2.1))
    Console.WriteLine(Math.Truncate(2.7))
    Console.WriteLine(Math.Truncate(-2.7))
    Console.WriteLine(Math.Round(2.5))
    Console.WriteLine(Math.Round(3.5))
    Console.WriteLine(Math.Round(-2.5))
    Console.WriteLine(Math.Sqrt(0.0))
}", "e2e-math-primitives", (TargetPlatform)platform);

            // round 为 banker's rounding（最近偶数）：2.5→2、3.5→4、-2.5→-2
            Assert.Equal(
                "1.414214\r\n" +
                "2\r\n" +
                "-3\r\n" +
                "3\r\n" +
                "-2\r\n" +
                "2\r\n" +
                "-2\r\n" +
                "2\r\n" +
                "4\r\n" +
                "-2\r\n" +
                "0\r\n", stdout);
            Assert.Equal(0, exitCode);
        }
        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Beep_EndToEnd(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    Runtime.Beep(800, 50)
    Console.WriteLine(""beeped"")
}", "e2e-beep", (TargetPlatform)platform);

            Assert.Equal(0, exitCode);
            Assert.Equal("beeped\r\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Stdlib_StringExtensions_EndToEnd(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    Console.WriteLine(String.Replace(""hello world"", ""world"", ""there""))
    Console.WriteLine(String.Replace(""aaa"", ""aa"", ""b""))
    Console.WriteLine(String.PadLeft(""7"", 3, '0'))
    Console.WriteLine(String.PadRight(""7"", 3, '0'))
    var parts = String.Split(""a,b,c"", ',')
    Console.WriteLine(parts.Length)
    Console.WriteLine(parts[0])
    Console.WriteLine(parts[1])
    Console.WriteLine(parts[2])
    var arr = new i32[] {3, 1, 2}
    Console.WriteLine(Array.Average(arr))
    var darr = new f64[] {1.5, 2.5}
    Console.WriteLine(Array.Average(darr))
}", "e2e-stdlib-extensions", (TargetPlatform)platform);

            Assert.Equal(
                "hello there\r\n" +
                "ba\r\n" +
                "007\r\n" +
                "700\r\n" +
                "3\r\n" +
                "a\r\n" +
                "b\r\n" +
                "c\r\n" +
                "2\r\n" +
                "2\r\n", stdout);
            Assert.Equal(0, exitCode);
        }
    }
}
