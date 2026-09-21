using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;

namespace Cocoa.Tests.Compiler
{
    /// <summary>
    /// M5-a5 批 2：受限端到端——纯算术顶层 `Main(): i32` 走完整自举链（Binder → IlEmitter/IlAssembler →
    /// 元数据计划 → IlMetadataBuilder 五流 → ManagedPeWriter）产出物理 .dll，实跑断言返回值。
    /// 输入以 C# 求值驱动自举 IlDriver.BuildDllHex（仿其他差分测试）；装配加载 + dotnet 子进程双验证。
    /// </summary>
    public class SelfHostedEndToEndTests
    {
        [Theory]
        [InlineData("function Main(): i32 { return 1 + 2 }", 3)]
        [InlineData("function Main(): i32 { return 6 * 7 }", 42)]
        [InlineData("function Main(): i32 { return 100 / 4 }", 25)]
        [InlineData("function Main(): i32 { return 10 - 4 }", 6)]
        [InlineData("function Main(): i32 { return 2 * 3 + 4 }", 10)]
        [InlineData("function Main(): i32 { var x = 5 x = x + 3 return x }", 8)]
        [InlineData("function Add(a: i32, b: i32): i32 { return a + b } function Main(): i32 { return Add(6, 7) }", 13)]
        [InlineData("function Main(): i32 { var x = 0 while x < 3 { x = x + 1 } return x }", 3)]
        [InlineData("function Main(): i32 { if 2 > 1 { return 5 } return 7 }", 5)]
        public void SelfHosted_PureArithmetic_Main_Runs_In_ProducedDll(string source, int expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-e2e", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var peHex = RunSelfDriver(source);
                Assert.False(peHex.StartsWith("ERR:", StringComparison.Ordinal), "自举链报错：" + peHex);
                Assert.True(peHex.Length > 0 && peHex.Length % 2 == 0, "PE hex 异常长度 " + peHex.Length);

                var dllPath = Path.Combine(dir, "Min.dll");
                File.WriteAllBytes(dllPath, HexToBytes(peHex));

                // 验证 1：Assembly.LoadFile + 入口点调用
                var assembly = Assembly.LoadFile(dllPath);
                var entryPoint = assembly.EntryPoint;
                Assert.NotNull(entryPoint);
                var result = entryPoint!.Invoke(null, null);
                Assert.Equal(expected, (int)result!);

                // 验证 2：dotnet 子进程实跑（写 runtimeconfig 以解析共享框架）
                File.WriteAllText(Path.Combine(dir, "Min.runtimeconfig.json"),
                    "{\"runtimeOptions\":{\"tfm\":\"net9.0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"9.0.0\"}}}");
                var psi = new System.Diagnostics.ProcessStartInfo("dotnet", dllPath)
                {
                    WorkingDirectory = dir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var process = System.Diagnostics.Process.Start(psi)!;
                process.WaitForExit(30000);
                Assert.True(process.HasExited, "dotnet 未在 30s 内退出");
                Assert.Equal(expected, process.ExitCode);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Theory]
        [InlineData("function Main(): i32 { Console.WriteLine(42) return 0 }", 0, "42")]
        [InlineData("function Main(): i32 { Console.WriteLine(\"Hello\") return 0 }", 0, "Hello")]
        public void SelfHosted_BclCall_WriteLine_PrintsToStdout(string source, int expectedExit, string expectedOutput)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-e2e", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var peHex = RunSelfDriver(source);
                Assert.False(peHex.StartsWith("ERR:", StringComparison.Ordinal), "自举链报错：" + peHex);

                var dllPath = Path.Combine(dir, "Min.dll");
                File.WriteAllBytes(dllPath, HexToBytes(peHex));
                File.WriteAllText(Path.Combine(dir, "Min.runtimeconfig.json"),
                    "{\"runtimeOptions\":{\"tfm\":\"net9.0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"9.0.0\"}}}");

                var psi = new System.Diagnostics.ProcessStartInfo("dotnet", dllPath)
                {
                    WorkingDirectory = dir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var process = System.Diagnostics.Process.Start(psi)!;
                process.WaitForExit(30000);
                Assert.True(process.HasExited, "dotnet 未在 30s 内退出");
                Assert.Equal(expectedExit, process.ExitCode);
                var stdout = process.StandardOutput.ReadToEnd().Trim();
                Assert.Contains(expectedOutput, stdout);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        // ------------------------------------------------------------------
        // 自举驱动
        // ------------------------------------------------------------------

        private static string RunSelfDriver(string source)
        {
            var root = RepoRoot();
            var compilerDir = Path.Combine(root, "src", "Cocoa.Co", "Cocoa.Compiler");
            var trees = ImmutableArray.CreateBuilder<SyntaxTree>();
            foreach (var file in Directory.GetFiles(compilerDir, "*.co", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal))
            {
                trees.Add(SyntaxTree.Parse(File.ReadAllText(file)));
            }

            // .co 字符串字面量转义：反斜杠 → \\、引号 → \"（源里含字符串字面量时需转义）。
            var esc = source.Replace("\\", "\\\\").Replace("\"", "\\\"");
            trees.Add(SyntaxTree.Parse($@"using Cocoa.CodeGen
using System

function Main(): i32
{{
    let result = Cocoa.CodeGen.IlDriver.BuildDllHex(""{esc}"")
    System.Console.WriteLine(""R:"" + result)
    return 0
}}"));

            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create(
                    "Main",
                    new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location },
                    trees.ToArray());
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                if (result.Diagnostics.HasErrors())
                {
                    Assert.True(false, "COCOMPILE-ERROR: " + string.Join(" | ", result.Diagnostics.Select(d => d.Message)));
                }

                var line = writer.ToString().Replace("\r\n", "\n").Split('\n')
                    .Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("R:", StringComparison.Ordinal));
                return line == null ? "ERR:<no R: output>" : line[2..];
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        private static byte[] HexToBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)Convert.ToInt32(hex.Substring(i * 2, 2), 16);
            }

            return bytes;
        }

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "src", "Cocoa.SDK", "System.Core", "String.co")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            Assert.NotNull(dir);
            return dir!;
        }
    }
}