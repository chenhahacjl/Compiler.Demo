using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Bound;
using Cocoa.CodeAnalysis.Contracts;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Targeting;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Contracts
{
    /// <summary>
    /// 闭包捕获语义契约（3.1）：捕获变量堆存储规范三后端一致。
    /// 契约断言：<see cref="ClosureContract"/> 环境类命名/捕获集判定；
    /// 行为断言：两个宿主实例捕获独立（三后端输出一致）。
    /// </summary>
    public class ClosureContractTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string IndependentCounterProgram = @"using System

function MakeAdder(n: i32): (i32) -> i32
{
    let f = (x: i32) => x + n
    return f
}

function Main(): i32
{
    let addA = MakeAdder(10)
    let addB = MakeAdder(100)

    Console.WriteLine(addA(1))
    Console.WriteLine(addB(1))
    Console.WriteLine(addA(2))
    Console.WriteLine(addB(2))
    return 0
}";

        private const string Expected = "11\n101\n12\n102\n";

        public static IEnumerable<object[]> GetBackends()
        {
            yield return new object[] { "evaluator" };
            yield return new object[] { "native-x64" };
            yield return new object[] { "il" };
        }

        [Theory]
        [MemberData(nameof(GetBackends))]
        public void CapturedVariables_AreIndependentPerHost_ThreeBackends(string backend)
        {
            if (backend == "evaluator")
            {
                var original = Console.Out;
                try
                {
                    using var writer = new StringWriter();
                    Console.SetOut(writer);
                    var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(IndependentCounterProgram));
                    var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                    Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                    Assert.Equal(Expected, writer.ToString().Replace("\r\n", "\n"));
                }
                finally
                {
                    Console.SetOut(original);
                }

                return;
            }

            if (backend == "il")
            {
                var (exitCode, stdout) = Emit.IL.IlE2eTests.EmitAndRun(IndependentCounterProgram, "ClosureContract");
                Assert.Equal(0, exitCode);
                Assert.Equal(Expected, stdout.Replace("\r\n", "\n"));
                return;
            }

            var platform = new TargetPlatform(TargetOS.Windows, Architecture.X64);
            var tree = SyntaxTree.Parse(IndependentCounterProgram);
            var compilationNative = Compilation.Create(tree);
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-closure-contract-tests");
            Directory.CreateDirectory(directory);
            var exePath = Path.Combine(directory, "ClosureContract-x64.exe");
            var diagnostics = compilationNative.EmitNative("ClosureContract", exePath, platform);

            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));
            Assert.True(File.Exists(exePath));

            var psi = new System.Diagnostics.ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);

            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new TimeoutException("Native exe did not exit in time.");
            }

            outputTask.Wait();
            var nativeStdout = Encoding.Unicode.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(Expected, nativeStdout);
        }

        // ---- 契约单元断言 -----------------------------------------------

        [Fact]
        public void EnvironmentClassNaming_MatchesBinderConvention()
        {
            var environmentClass = new NamedTypeSymbol("__Env_MakeCounter", "MyLib", Visibility.Public, declaration: null);
            var plainClass = new NamedTypeSymbol("Point", "MyLib", Visibility.Public, declaration: null);

            Assert.True(ClosureContract.IsEnvironmentClass(environmentClass));
            Assert.False(ClosureContract.IsEnvironmentClass(plainClass));
        }

        [Fact]
        public void RequiresEnvironment_TrueWhenCapturedVariablesPresent()
        {
            var withCapture = new FunctionSymbol("f", ImmutableArray<ParameterSymbol>.Empty, TypeSymbol.Int32, null);
            withCapture.CapturedVariables = new List<VariableSymbol> { new LocalVariableSymbol("x", true, TypeSymbol.Int32, null) };
            Assert.True(ClosureContract.RequiresEnvironment(withCapture));

            var noCapture = new FunctionSymbol("g", ImmutableArray<ParameterSymbol>.Empty, TypeSymbol.Int32, null);
            Assert.False(ClosureContract.RequiresEnvironment(noCapture));
        }
    }
}
