using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.Targeting;
using Cocoa.CodeGen.Native;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Tests.CodeAnalysis.Emit.Native;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 可选参数（语言后置件 4/8）：形参默认值 `x: i32 = 10`，调用缺省实参自动补默认值字面量（三后端复用字面量发射）。
    /// 命名参数为同项后续子集（Cocoa 动手前校验实参含 `名: 值` 时按形参名重排）。
    /// </summary>
    public class OptionalParameterThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Template = @"using System

function Add(x: i32, y: i32 = 10): i32 { return x + y }
function Add(a: string): string { return a + ""-s"" }
function Greet(name: string, quote: string = ""!""): string { return name + quote }

public class Point
{
    public x: i32
    public y: i32
    public constructor(px: i32, py: i32 = 5) { x = px; y = py }
    public function Sum(a: i32, b: i32 = 7): i32 { return a + b }
}

function Main(): i32
{
    System.Console.WriteLine(Add(1) == 11)
    System.Console.WriteLine(Add(1, 2) == 3)
    System.Console.WriteLine(Add(""z"") == ""z-s"")
    System.Console.WriteLine(Greet(""hi"") == ""hi!"")
    var p = new Point(1)
    System.Console.WriteLine(p.y == 5)
    System.Console.WriteLine(p.Sum(3) == 10)
    return 0
}";

        private const string Expected = "True\nTrue\nTrue\nTrue\nTrue\nTrue\n";

        [Fact]
        public void Evaluator_OptionalParameters()
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
        public void IlE2e_OptionalParameters()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-opt", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "opt.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.Emit("opttest", References(), exePath, IlTarget.Parse("net9.0"));
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
        public void NativeE2e_OptionalParameters(string target)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-opt", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "opt-" + target + ".exe");
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.EmitNative("optsdk", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(Expected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
        }
    }
}