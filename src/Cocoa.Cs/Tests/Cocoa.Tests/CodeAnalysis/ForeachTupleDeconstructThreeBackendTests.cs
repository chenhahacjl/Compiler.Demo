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
    /// <summary>元组 foreach 解构变量（语言后置件增量）：`foreach (var (a, b) in items)` 按 ItemN 逐字段声明循环变量。</summary>
    public class ForeachTupleDeconstructThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string ArrayTemplate = @"using System

function Main(): i32
{
    var pairs = [(1, ""one""), (2, ""two""), (3, ""three"")]

    var sum = 0
    var concat = """"
    foreach (var (id, name) in pairs)
    {
        sum += id
        concat += name
    }
    System.Console.WriteLine(sum)
    System.Console.WriteLine(concat)
    return 0
}";

        private const string ArrayExpected = "6\nonetwothree\n";

        private const string ThreeTupleTemplate = @"using System

function Main(): i32
{
    var items = [(10, ""a"", true), (20, ""b"", false)]

    var sum = 0
    var concat = """"
    var flag = true
    foreach (var (n, s, f) in items)
    {
        sum += n
        concat += s
        flag = flag && f
    }
    System.Console.WriteLine(sum)
    System.Console.WriteLine(concat)
    System.Console.WriteLine(flag)
    return 0
}";

        private const string ThreeTupleExpected = "30\nab\nFalse\n";

        [Fact]
        public void Evaluator_ForeachTupleDeconstruct_TwoElement()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(ArrayTemplate));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(ArrayExpected, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void Evaluator_ForeachTupleDeconstruct_ThreeElement()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(ThreeTupleTemplate));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(ThreeTupleExpected, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void IlE2e_ForeachTupleDeconstruct_TwoElement()
        {
            AssertIl(ArrayTemplate, "ftd-arr", ArrayExpected);
        }

        [Fact]
        public void IlE2e_ForeachTupleDeconstruct_ThreeElement()
        {
            AssertIl(ThreeTupleTemplate, "ftd-tri", ThreeTupleExpected);
        }

        private static void AssertIl(string source, string name, string expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-ftd", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, name + ".exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
            var diagnostics = compilation.Emit(name, References(), exePath, IlTarget.Parse("net9.0"));
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
            Assert.Equal(expected, stdout);
        }

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_ForeachTupleDeconstruct_TwoElement(string target)
        {
            AssertNative(ArrayTemplate, "ftd-arr-" + target, target, ArrayExpected);
        }

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_ForeachTupleDeconstruct_ThreeElement(string target)
        {
            AssertNative(ThreeTupleTemplate, "ftd-tri-" + target, target, ThreeTupleExpected);
        }

        private static void AssertNative(string source, string name, string target, string expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-ftd", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, name + ".exe");
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
            var diagnostics = compilation.EmitNative("ftdsdk", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(expected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
        }
    }
}