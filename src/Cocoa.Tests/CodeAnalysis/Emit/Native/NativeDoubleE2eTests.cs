using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.Native;
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
            var (exitCode, stdout) = EmitNativeAndRun(@"
function main()
{
    var d: double = 3.14
    print(d)
    print(1.5 + 2.25)
    print(10.0 / 4)
    print(2.5 * 2)
    print(7 - 1.5)
    print(1.5 < 2.5)
    print(1.5 == 1.5)
    print((int)3.9)
    print((byte)3.9)
    print((double)3)
    var arr: double[] = new double[2] {1.5, 2.5}
    arr[0] = 3.5
    print(arr[0])
    print(arr[1])
    var sum: double = 0.0
    sum = sum + arr[0]
    print(sum)
}", "e2e-double", (TargetPlatform)platform);

            Assert.Equal(0, exitCode);
            Assert.Equal("3.14\r\n3.75\r\n2.5\r\n5\r\n5.5\r\nTrue\r\nTrue\r\n3\r\n3\r\n3\r\n3.5\r\n2.5\r\n3.5\r\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Double_FixedPointFormatting_AndSpecialValues(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"
function main()
{
    print(0.0)
    print(-1.5)
    print(1.0 / 3.0)
    print(2.0 / 3.0)
    print(100.0 / 7)
    print(123456789.0)
    print(1.0 / 0.0)
    print(0.0 / 0.0)
    print(""d="" + 1.5)
    print((string)2.75)
    var x: double = -0.0
    print(x)
    print(0.1 + 0.2)
}", "e2e-double-fmt", (TargetPlatform)platform);

            Assert.Equal(0, exitCode);
            Assert.Equal("0\r\n-1.5\r\n0.333333\r\n0.666667\r\n14.285714\r\n123456789\r\nInfinity\r\nNaN\r\nd=1.5\r\n2.75\r\n-0\r\n0.3\r\n", stdout);
        }
    }
}
