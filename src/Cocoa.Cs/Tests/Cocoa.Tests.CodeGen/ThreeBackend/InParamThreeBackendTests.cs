using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Binder;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.Targeting;
using Cocoa.CodeGen.Native;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Tests.CodeGen.Emit.Native;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeGen.ThreeBackend
{
    /// <summary>
    /// `in` 修饰符（C# 7.2，2026-10-20）：只读传值参数——值拷贝 ABI 零发射改动；
    /// 绑定期禁止对 in 参数赋值、禁止作 out/ref 实参（<see cref="VariableSymbol.IsReadOnly"/> 诊断）。
    /// 与 <see cref="ParameterSymbol.IsThisParameter"/> 解耦（this 恒只读但非 in）。
    /// </summary>
    public class InParamThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Template = @"using System

function Add(in a: i32, b: i32): i32 { return a + b }

function Main(): i32
{
    System.Console.WriteLine(Add(1, 2))
    return 0
}";

        private const string Expected = "3\n";

        [Fact]
        public void Evaluator_InParam_ReadOnlyValueReadsFine()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(Expected, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void IlE2e_InParam_ReadOnlyValueReadsFine()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-inparam", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "inparam.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.Emit("intst", References(), exePath, IlTarget.Parse("net9.0"));
            Assert.Empty(string.Join("\n", diagnostics));
            var psi = new ProcessStartInfo("dotnet", $"\"{exePath}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            using var errReader = process.StandardError;
            var errTask = errReader.ReadToEndAsync();
            Assert.True(process.WaitForExit(20000));
            outputTask.Wait();
            var stdout = Encoding.UTF8.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            Assert.True(process.ExitCode == 0, "exit=" + process.ExitCode + " stderr=" + errTask.Result.Replace("\n", " | "));
            Assert.Equal(Expected, stdout);
        }

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_InParam_ReadOnlyValueReadsFine(string target)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-inparam", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "inparam-" + target + ".exe");
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.EmitNative("intst", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(Expected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
        }

        [Fact]
        public void InParam_Assignment_ReportsReadOnly()
        {
            var source = @"function F(in a: i32): i32 { a = 5; return a }
function Main(): i32 { return F(1) }";
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
            var diagnostics = compilation.GetDiagnostics();
            Assert.True(diagnostics.Any(d => d.Message.Contains("'a' is read-only")), string.Join("\n", diagnostics.Select(d => d.Message)));
        }

        [Fact]
        public void InParam_AsOutArgument_Rejected()
        {
            var source = @"function Set(out x: i32): void { x = 1 }
function F(in a: i32): i32 { Set(out a); return a }
function Main(): i32 { return F(1) }";
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
            var diagnostics = compilation.GetDiagnostics();
            Assert.True(diagnostics.HasErrors(), string.Join("\n", diagnostics.Select(d => d.Message)));
        }

        [Fact]
        public void InParam_AsRefArgument_Rejected()
        {
            var source = @"function Bump(ref x: i32): void { x = x + 1 }
function F(in a: i32): i32 { Bump(ref a); return a }
function Main(): i32 { return F(1) }";
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
            var diagnostics = compilation.GetDiagnostics();
            Assert.True(diagnostics.HasErrors(), string.Join("\n", diagnostics.Select(d => d.Message)));
        }

        [Fact]
        public void CoaRoundTrip_InParam_KeepsReadOnly()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-inparam", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var coaPath = Path.Combine(dir, "Lib.coa");
            var libSource = @"namespace Lib {
    public function Add(in a: i32, b: i32): i32 { return a + b }
}";
            var libDiags = Compilation.Create(SyntaxTree.Parse(libSource)).EmitCoa("Lib", coaPath);
            Assert.True(!libDiags.HasErrors(), string.Join("\n", libDiags.Select(d => d.Message)));

            var external = SystemLibrary.Load();
            var coa = CoaSerializer.Load(coaPath, external);
            var add = coa.Functions.FirstOrDefault(f => f.Name == "Add");
            Assert.NotNull(add);
            Assert.True(add!.Parameters[0].IsReadOnly, "in 参数 round-trip 后应保持只读");
            Assert.False(add!.Parameters[0].IsThisParameter, "in 参数不得误判为 this");
        }
    }
}