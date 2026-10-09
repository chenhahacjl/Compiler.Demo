using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Targeting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// decimal / half（第二批数值类型）全链路：字面量后缀（m/M）、转换矩阵、算术/比较、
    /// Evaluator 求值、IL（System.Decimal op_* / System.Half op_Explicit）、native 明确拒绝。
    /// </summary>
    public class DecimalHalfTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        // ---- Evaluator ---------------------------------------------------------

        [Fact]
        public void Evaluator_Decimal_Literal_Arithmetic_Compare()
        {
            var text = @"using System

function Main(): i32
{
    var a: decimal = 3.14m
    var b: decimal = 1.5m + 2.25m
    var c: decimal = 5m * 2m
    var d: decimal = 1m / 4m
    var e: decimal = 10m % 3m
    Console.WriteLine(a)
    Console.WriteLine(b)
    Console.WriteLine(c)
    Console.WriteLine(d)
    Console.WriteLine(e)
    Console.WriteLine(1.5m < 2m)
    Console.WriteLine(1.5m == 1.50m)
    Console.WriteLine(-3.5m)
    return 0
}";
            var (stdout, diagnostics) = Evaluate(text);
            Assert.True(!diagnostics.HasErrors(), string.Join("\n", diagnostics.Select(d => d.Message)));
            Assert.Equal("3.14\n3.75\n10\n0.25\n1\nTrue\nTrue\n-3.5\n", stdout);
        }

        [Fact]
        public void Evaluator_Decimal_Conversions()
        {
            var text = @"using System

function Main(): i32
{
    var fromInt: decimal = 5
    var fromLong: decimal = 5000000000
    var truncated: i32 = (i32)3.99m
    var asDouble: f64 = (f64)2.5m
    Console.WriteLine(fromInt)
    Console.WriteLine(fromLong)
    Console.WriteLine(truncated)
    Console.WriteLine(asDouble)
    return 0
}";
            var (stdout, diagnostics) = Evaluate(text);
            Assert.True(!diagnostics.HasErrors(), string.Join("\n", diagnostics.Select(d => d.Message)));
            Assert.Equal("5\n5000000000\n3\n2.5\n", stdout);
        }

        [Fact]
        public void Evaluator_Half_Conversions()
        {
            var text = @"using System

function Main(): i32
{
    var h: half = (half)1.5
    var f: f32 = (f32)h
    var d: f64 = (f64)h
    Console.WriteLine(h)
    Console.WriteLine(f)
    Console.WriteLine(d)
    return 0
}";
            var (stdout, diagnostics) = Evaluate(text);
            Assert.True(!diagnostics.HasErrors(), string.Join("\n", diagnostics.Select(d => d.Message)));
            Assert.Equal("1.5\n1.5\n1.5\n", stdout);
        }

        // ---- IL ---------------------------------------------------------------

        [Fact]
        public void IlE2e_Decimal_Arithmetic()
        {
            var output = EmitIlAndRun(@"using System

function Main(): i32
{
    var a: decimal = 3.14m
    var b: decimal = 1.5m + 2.25m
    var c: decimal = 5m * 2m
    var d: decimal = 1m / 4m
    var e: decimal = 10m % 3m
    Console.WriteLine(a)
    Console.WriteLine(b)
    Console.WriteLine(c)
    Console.WriteLine(d)
    Console.WriteLine(e)
    Console.WriteLine(1.5m < 2m)
    Console.WriteLine(1.5m == 1.50m)
    Console.WriteLine(-3.5m)
    Console.WriteLine((i32)3.99m)
    return 0
}");
            Assert.Equal("3.14\n3.75\n10\n0.25\n1\nTrue\nTrue\n-3.5\n3\n", output);
        }

        [Fact]
        public void IlE2e_Half_Conversions()
        {
            var output = EmitIlAndRun(@"using System

function Main(): i32
{
    var h: half = (half)1.5
    var f: f32 = (f32)h
    var d: f64 = (f64)h
    Console.WriteLine(h)
    Console.WriteLine(f)
    Console.WriteLine(d)
    return 0
}");
            Assert.Equal("1.5\n1.5\n1.5\n", output);
        }

        // ---- Native（编译期明确拒绝）-----------------------------------------

        [Fact]
        public void Native_Decimal_Rejected()
        {
            var syntaxTree = SyntaxTree.Parse(@"using System

function Main(): i32
{
    var d: decimal = 1.5m
    Console.WriteLine(d)
    return 0
}");
            var compilation = Compilation.Create(syntaxTree);
            var diagnostics = compilation.EmitNative("dec", Path.Combine(Path.GetTempPath(), "cocoa-native-dec.exe"), new TargetPlatform(TargetOS.Windows, Architecture.X64));
            Assert.Contains(diagnostics, d => d.Message.Contains("decimal"));
        }

        [Fact]
        public void Native_Half_Rejected()
        {
            var syntaxTree = SyntaxTree.Parse(@"using System

function Main(): i32
{
    var h: half = (half)1.5
    Console.WriteLine(h)
    return 0
}");
            var compilation = Compilation.Create(syntaxTree);
            var diagnostics = compilation.EmitNative("half", Path.Combine(Path.GetTempPath(), "cocoa-native-half.exe"), new TargetPlatform(TargetOS.Windows, Architecture.X64));
            Assert.Contains(diagnostics, d => d.Message.Contains("half"));
        }

        // ---- helpers ----------------------------------------------------------

        private static (string Stdout, IEnumerable<Diagnostic> Diagnostics) Evaluate(string text)
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(text));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                return (writer.ToString().Replace("\r\n", "\n"), result.Diagnostics);
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        private static string EmitIlAndRun(string source)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-dec-half-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "t.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
            var diagnostics = compilation.Emit("t", References(), exePath, IlTarget.Parse("net9.0"));
            Assert.True(!diagnostics.HasErrors(), string.Join("\n", diagnostics.Select(d => d.Message)));

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
            return stdout;
        }
    }
}
