using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeGen.PE;
 using Cocoa.Targeting;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native
{
    /// <summary>
    /// 原生发射管线冒烟（4.4：旧 RuntimeEmitterX64/X86 白盒测试迁 LIR 生产管线）。
    /// PE 头断言 + 运行时函数（打印/拼接/相等/输入）经语言层等价覆盖。
    /// </summary>
    public class NativeEmitTests
    {
        public static IEnumerable<object[]> GetPlatforms()
        {
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X64) };
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X86) };
        }

        private static string GetExePath(string name, TargetPlatform platform)
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-native-tests");
            Directory.CreateDirectory(directory);
            var suffix = platform.Arch == Architecture.X86 ? "-x86" : "";
            return Path.Combine(directory, name + suffix + ".exe");
        }

        private static (int ExitCode, string Stdout) EmitNativeAndRun(string source, string name, TargetPlatform platform, string? input = null)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var exePath = GetExePath(name, platform);
            var diagnostics = compilation.EmitNative(name, exePath, platform);

            Assert.True(diagnostics.IsEmpty, string.Join("\n", System.Linq.Enumerable.Select(diagnostics, d => d.Message)));
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardInput = input != null,
                UseShellExecute = false,
            };

            using var process = Process.Start(psi)!;

            if (input != null)
            {
                var inputBytes = Encoding.Unicode.GetBytes(input);
                process.StandardInput.BaseStream.Write(inputBytes, 0, inputBytes.Length);
                process.StandardInput.BaseStream.Close();
            }

            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);

            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new TimeoutException("Native exe did not exit in time.");
            }

            outputTask.Wait();
            var bytes = output.ToArray();
            var stdout = Encoding.Unicode.GetString(bytes).Replace("\r\n", "\n").Replace("\r", "\n");
            return (process.ExitCode, stdout);
        }

        /// <summary>运行原生 exe 并返回 Unicode stdout（断言退出码）；供其他 native 测试复用。</summary>
        internal static string Run(string exePath, string? input = null, int expectedExitCode = 0)
        {
            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardInput = input != null,
                UseShellExecute = false,
            };


            using var process = Process.Start(psi)!;
            if (input != null)
            {
                var inputBytes = Encoding.Unicode.GetBytes(input);
                process.StandardInput.BaseStream.Write(inputBytes, 0, inputBytes.Length);
                process.StandardInput.BaseStream.Close();
            }

            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);

            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new TimeoutException("Native exe did not exit in time.");
            }

            outputTask.Wait();
            var bytes = output.ToArray();

            Assert.Equal(expectedExitCode, process.ExitCode);
            return Encoding.Unicode.GetString(bytes);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void NativeExe_HasValidPeHeaders(object platform)
        {
            var target = (TargetPlatform)platform;
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    Console.WriteLine(""ok"")
}", "pe-headers", target);

            Assert.Equal(0, exitCode);
            Assert.Equal("ok\n", stdout);

            var bytes = File.ReadAllBytes(GetExePath("pe-headers", target));
            Assert.Equal(new byte[] { 0x4D, 0x5A }, new[] { bytes[0], bytes[1] });
            var peOffset = BitConverter.ToInt32(bytes, 0x3C);
            Assert.Equal("PE", Encoding.ASCII.GetString(bytes, peOffset, 2));
            var machine = BitConverter.ToUInt16(bytes, peOffset + 4);
            Assert.Equal(
                target.Arch == Architecture.X64 ? 0x8664 : 0x014C,
                machine);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void NativeExe_RuntimeSmoke(object platform)
        {
            var target = (TargetPlatform)platform;
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

function Main()
{
    Console.WriteLine(42)
    Console.WriteLine(-7)
    Console.WriteLine(""foo"" + ""bar"")
    Console.WriteLine(""foo"" == ""foo"")
    Console.WriteLine(""foo"" == ""bar"")
    var s = Console.ReadLine()
    Console.WriteLine(s)
}", "runtime-smoke", target, input: "AB\n");

            Assert.Equal(0, exitCode);
            Assert.Equal("42\n-7\nfoobar\nTrue\nFalse\nAB\n", stdout);
        }
    }
}
