using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.IL;
using Cocoa.CodeAnalysis.Emit.Native;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// Y §6.7 A1：语义标志（IsLambda / IsPropertyAccessor）验证锚——
    /// 属性访问器经符号面直接断言；提升 lambda 不在公开 Function 清单，改经 IL/native 往返
    /// （IsLambda 决定 env-first/env-instance 发射，标志缺失即输出错误/非法程序），并断言普通函数不带标志。
    /// </summary>
    public class SemanticFunctionFlagTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        [Fact]
        public void PropertyAccessorFunctions_HaveIsPropertyAccessor()
        {
            var tree = SyntaxTree.Parse("class Foo\n{\n    private _x: i32\n    public property X: i32\n    {\n        get\n        {\n            return _x\n        }\n        set\n        {\n            _x = value\n        }\n    }\n}");
            var compilation = Compilation.Create("Main", References(), tree);

            var getter = compilation.Functions.FirstOrDefault(f => f.Name == "get_X");
            Assert.NotNull(getter);
            Assert.True(getter!.IsPropertyAccessor);

            var setter = compilation.Functions.FirstOrDefault(f => f.Name == "set_X");
            Assert.NotNull(setter);
            Assert.True(setter!.IsPropertyAccessor);
        }

        [Fact]
        public void NonLambdaFunction_DoesNotHaveIsLambda()
        {
            var tree = SyntaxTree.Parse("function Main(): i32 { return 0 }");
            var compilation = Compilation.Create("Main", References(), tree);

            var main = compilation.Functions.FirstOrDefault(f => f.Name == "Main");
            Assert.NotNull(main);
            Assert.False(main!.IsLambda);
            Assert.False(main.IsPropertyAccessor);
        }

        [Fact]
        public void Lambda_TypedNonCapturing_RoundTrips_Il()
        {
            var source = "using System\nfunction Main(): i32\n{\n    var f: (i32) -> i32 = (x: i32) => x * 2\n    System.Console.WriteLine(f(21))\n    return 0\n}";
            using var run = EmitAndRun(source, IlTarget.Parse("net9.0"), "lambda", il: true);
            Assert.Equal(0, run.ExitCode);
            Assert.Equal("42\n", run.Stdout);
        }

        [Fact]
        public void CapturingLambda_RoundTrips_NativeX64()
        {
            var source = "using System\nfunction Main(): i32\n{\n    var n = 40\n    let f = () => n + 2\n    System.Console.WriteLine(f())\n    return 0\n}";
            using var run = EmitAndRun(source, new TargetPlatform(TargetOS.Windows, Architecture.X64), "lambda", il: false);
            Assert.Equal(0, run.ExitCode);
            Assert.Equal("42\n", run.Stdout);
        }

        private sealed class RunResult : IDisposable
        {
            public int ExitCode;
            public string Stdout = "";
            private readonly Process? _process;
            public RunResult(Process? process) { _process = process; }
            public void Dispose() => _process?.Dispose();
        }

        private static RunResult EmitAndRun(string source, object target, string name, bool il)
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-a1-flags");
            Directory.CreateDirectory(directory);
            var exePath = Path.Combine(directory, (il ? "lam-il-" : "lam-nt-") + Guid.NewGuid().ToString("N") + ".exe");

            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
            if (il)
            {
                var diagnostics = compilation.Emit(name, References(), exePath, (IlTarget)target);
                Assert.Empty(string.Join("\n", diagnostics));
            }
            else
            {
                var diagnostics = compilation.EmitNative(name, exePath, (TargetPlatform)target);
                Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));
            }

            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo(il ? "dotnet" : exePath, il ? $"\"{exePath}\"" : "")
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
                throw new TimeoutException("exe did not exit in time.");
            }

            outputTask.Wait();
            var stdout = Encoding.UTF8.GetString(output.ToArray());
            if (!il)
            {
                stdout = Encoding.Unicode.GetString(output.ToArray());
            }

            return new RunResult(process) { ExitCode = process.ExitCode, Stdout = stdout.Replace("\r\n", "\n") };
        }
    }
}