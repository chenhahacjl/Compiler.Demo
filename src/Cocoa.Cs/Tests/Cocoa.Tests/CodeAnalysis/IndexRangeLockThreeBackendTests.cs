using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Targeting;
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
    /// N1：Index/Range 切片与 lock 语句三后端对齐。
    /// binder 将 arr[^n] / arr[i..j] 降级为 GetOffset / GetOffsetAndLength + CopyRange（三后端共享零特判）；
    /// lock 降级为 zero-catch try/finally（native 以 finally 克隆语义支持：return / continue 跳出前先执行 finally）。
    /// </summary>
    public class IndexRangeLockThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string IndexTemplate = @"using System

function Main(): i32
{
    var arr = new i32[] { 10, 20, 30, 40 }
    System.Console.WriteLine(arr[^1])
    System.Console.WriteLine(arr[^4])
    arr[^2] = 99
    System.Console.WriteLine(arr[2])
    var i = Index.FromEnd(1)
    System.Console.WriteLine(arr[i])
    return 0
}";

        private const string IndexExpected = "40\n10\n99\n40\n";

        private const string RangeTemplate = @"using System

function Main(): i32
{
    var arr = new i32[] { 1, 2, 3, 4, 5 }
    var middle = arr[1..3]
    System.Console.WriteLine(middle.Length == 2)
    return 0
}";

        private const string RangeExpected = "True\n";

        private const string LockTemplate = @"using System

function Grab(obj: object): i32
{
    lock (obj)
    {
        return 7
    }
}

function Main(): i32
{
    var gate = new object()
    lock (gate)
    {
        System.Console.WriteLine(""locked"")
    }
    var i = 0
    while i < 3
    {
        i = i + 1
        lock (gate)
        {
            System.Console.WriteLine(i)
        }
    }
    System.Console.WriteLine(Grab(gate))
    return 0
}";

        private const string LockExpected = "locked\n1\n2\n3\n7\n";

        // ------------------------------------------------------------------
        // Evaluator
        // ------------------------------------------------------------------

        [Fact]
        public void Evaluator_IndexElementAccess()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(IndexTemplate));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(IndexExpected, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void Evaluator_RangeSlice()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(RangeTemplate));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(RangeExpected, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void Evaluator_LockStatement()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(LockTemplate));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(LockExpected, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }



        // ------------------------------------------------------------------
        // IL
        // ------------------------------------------------------------------

        [Fact]
        public void IlE2e_IndexElementAccess()
        {
            RunIl(IndexTemplate, "index", IndexExpected);
        }

        [Fact]
        public void IlE2e_RangeSlice()
        {
            RunIl(RangeTemplate, "range", RangeExpected);
        }

        [Fact]
        public void IlE2e_LockStatement()
        {
            RunIl(LockTemplate, "lock", LockExpected);
        }

        private static void RunIl(string template, string name, string expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-idxrange", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, name + ".exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(template));
            var diagnostics = compilation.Emit("idxrange", References(), exePath, IlTarget.Parse("net9.0"));
            Assert.True(diagnostics.IsEmpty, string.Join(" | ", diagnostics.Select(d => d.Message)));
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

        // ------------------------------------------------------------------
        // Native（x64 + x86）
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_IndexElementAccess(string target)
        {
            RunNative(IndexTemplate, "index", IndexExpected, target);
        }

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_RangeSlice(string target)
        {
            RunNative(RangeTemplate, "range", RangeExpected, target);
        }

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_LockStatement(string target)
        {
            RunNative(LockTemplate, "lock", LockExpected, target);
        }

        private static void RunNative(string template, string name, string expected, string target)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-idxrange", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, name + "-" + target + ".exe");
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(template));
            var diagnostics = compilation.EmitNative("idxrange", exePath, platform);
            Assert.True(diagnostics.IsEmpty, string.Join(" | ", diagnostics.Select(d => d.Message)));
            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(expected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
        }
    }
}
