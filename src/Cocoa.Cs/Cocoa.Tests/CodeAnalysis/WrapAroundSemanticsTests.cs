using Cocoa.CodeAnalysis;
using Cocoa.CodeGen.Native;
using Cocoa.CodeGen.PE;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Tests.CodeAnalysis.Emit.IL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// unchecked 回绕语义锁定（自举缺口分析 §5 前置，M0-2）：整数回绕加法/乘法、截断转换矩阵、
    /// u32 FNV-1a 哈希向量 × 三后端（Evaluator / IL / native x64）——native 天然回绕、IL add 不查溢出，
    /// 固化语义保证自举版编译器的汇编器位打包与哈希函数依赖成立。
    /// </summary>
    public class WrapAroundSemanticsTests
    {
        private const string Source = @"using System

function Fnv1a(text: string): u32
{
    var hash: u32 = 2166136261U
    for var i = 0 to text.Length - 1
    {
        hash = hash ^ u32(i32(text[i]))
        hash = hash * 16777619U
    }
    return hash
}

function Main()
{
    Console.WriteLine(i64(4000000000U + 4000000000U))
    Console.WriteLine(i64(3000000000U * 3U))
    Console.WriteLine(i64((u8)300))
    Console.WriteLine(i64((u32)4294967295UL))
    Console.WriteLine(i32((u16)70000))
    Console.WriteLine(i64((u64)-1))
    Console.WriteLine(Fnv1a(""""))
    Console.WriteLine(Fnv1a(""a""))
    Console.WriteLine(Fnv1a(""hello""))
    Console.WriteLine(Fnv1a(""Cocoa""))
}";

        private const string ExpectedOutput =
            "3705032704\n" +   // 4000000000U + 4000000000U 回绕
            "410065408\n" +    // 3000000000U * 3U 回绕
            "44\n" +           // (u8)300 截断
            "4294967295\n" +   // (u32)4294967295UL 无截断
            "4464\n" +         // (u16)70000 截断
            "-1\n" +           // (u64)-1 全 1
            "2166136261\n" +   // FNV-1a("")
            "3826002220\n" +   // FNV-1a("a")
            "1335831723\n" +   // FNV-1a("hello")
            "4197378984\n";    // FNV-1a("Cocoa")

        [Fact]
        public void Evaluator_WrapAround_Truncation_Fnv1a()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);

                var tree = SyntaxTree.Parse(Source);
                var compilation = Compilation.Create(tree);
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(ExpectedOutput, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void IlE2e_WrapAround_Truncation_Fnv1a()
        {
            var (exitCode, stdout) = IlE2eTests.EmitAndRun(Source, "wrap-il");
            Assert.Equal(0, exitCode);
            Assert.Equal(ExpectedOutput, stdout.Replace("\r\n", "\n"));
        }

        [Fact]
        public void NativeX64_WrapAround_Truncation_Fnv1a()
        {
            var (exitCode, stdout) = EmitNativeAndRun(Source, "wrap-native");
            Assert.Equal(0, exitCode);
            Assert.Equal(ExpectedOutput, stdout);
        }

        private static (int ExitCode, string Stdout) EmitNativeAndRun(string source, string name)
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-wrap-smoke");
            Directory.CreateDirectory(directory);
            var exePath = Path.Combine(directory, name + ".exe");

            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var diagnostics = compilation.EmitNative(name, exePath, new TargetPlatform(TargetOS.Windows, Architecture.X64));

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
    }
}