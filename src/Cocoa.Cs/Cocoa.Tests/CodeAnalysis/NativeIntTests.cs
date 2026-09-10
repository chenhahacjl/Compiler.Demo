using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeGen.Native;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// nint / nuint（原生整型，平台自适应）全链路锁定：类型解析、转换矩阵（int→nint 隐式 / i64→nint 显式 /
    /// int 字面量→nuint 隐式常量窄化）、相等比较（Raw == 0）、Evaluator 求值、IL（native int ELEMENT_TYPE_I/U）、
    /// native x64/x86（Addr 平台宽 ABI）、.coa round-trip（@nint/@nuint）、C# 方言接受。
    /// </summary>
    public class NativeIntTests
    {
        private const string Program = @"using System

function Main(): i32
{
    var a: nint = 0
    var b: nuint = 0
    Console.WriteLine(a == 0)
    Console.WriteLine(b == 0)
    var c: nint = 5
    Console.WriteLine(c == 5)
    Console.WriteLine((i64)c == 5)
    Console.WriteLine((i32)c == 5)
    var d: i64 = c
    Console.WriteLine(d == 5)
    var e: i32 = (i32)c
    Console.WriteLine(e == 5)
    Console.WriteLine(c == 0)
    return 0
}";

        private const string Expected = "True\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nFalse\n";
        private const string CodExpected = "True\nTrue\n";

        // ---- Evaluator -------------------------------------------------------

        [Fact]
        public void Evaluator_NativeInt_Comparisons()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);

                var compilation = Compilation.Create("Main", new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, SyntaxTree.Parse(Program));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());

                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(Expected, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        // ---- 绑定诊断（转换矩阵） --------------------------------------------

        [Fact]
        public void Bind_IntToNint_Implicit()
        {
            var diagnostics = Compilation.Create(SyntaxTree.Parse(@"function Main()
{
    var a: nint = 5
    Console.WriteLine(a)
}")).GetDiagnostics();

            Assert.Empty(diagnostics.Where(d => d.IsError));
        }

        [Fact]
        public void Bind_LongToNint_RequiresExplicit()
        {
            var diagnostics = Compilation.Create(SyntaxTree.Parse(@"function Main()
{
    var a: nint = 5L
}")).GetDiagnostics();

            Assert.Contains(diagnostics, d => d.IsError);
        }

        [Fact]
        public void Bind_UIntToNuint_Implicit()
        {
            var diagnostics = Compilation.Create(SyntaxTree.Parse(@"function Main()
{
    var a: nuint = 5U
    Console.WriteLine(a)
}")).GetDiagnostics();

            Assert.Empty(diagnostics.Where(d => d.IsError));
        }

        [Fact]
        public void Bind_IntLiteralToNuint_ImplicitConstant()
        {
            var diagnostics = Compilation.Create(SyntaxTree.Parse(@"function Main()
{
    var a: nuint = 0
    var b: nuint = 5
    Console.WriteLine(a == 0)
    Console.WriteLine(b == 0)
}")).GetDiagnostics();

            Assert.Empty(diagnostics.Where(d => d.IsError));
        }

        [Fact]
        public void Bind_NintToLong_Implicit()
        {
            var diagnostics = Compilation.Create(SyntaxTree.Parse(@"function Main()
{
    var a: nint = 5
    var b: i64 = a
    Console.WriteLine(b)
}")).GetDiagnostics();

            Assert.Empty(diagnostics.Where(d => d.IsError));
        }

        [Fact]
        public void Cs_Dialect_Nint_Accepted()
        {
            var diagnostics = Compilation.Create(SyntaxTree.ParseCs(@"using System;
public static void Main()
{
    nint n = 0;
    nuint u = 0U;
    Console.WriteLine(n == 0);
    Console.WriteLine(u == 0);
}")).GetDiagnostics();

            Assert.Empty(diagnostics.Where(d => d.IsError));
        }

        // ---- IL --------------------------------------------------------------

        [Fact]
        public void Il_E2e_NativeInt()
        {
            var (exitCode, stdout) = EmitIlAndRun(Program, "nint-il");
            Assert.Equal(0, exitCode);
            Assert.Equal(Expected, stdout.Replace("\r\n", "\n"));
        }

        private static (int ExitCode, string Stdout) EmitIlAndRun(string source, string name)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create("Main", new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, syntaxTree);
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-nint-il");
            Directory.CreateDirectory(directory);
            var exePath = Path.Combine(directory, name + ".exe");
            var diagnostics = compilation.Emit(name, new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, exePath, IlTarget.Parse("net9.0"));

            Assert.Empty(diagnostics);
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo("dotnet", "\"" + exePath + "\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);
            return (process.ExitCode, stdout);
        }

        // ---- native（x64 + x86） ---------------------------------------------

        public static IEnumerable<object[]> GetPlatforms()
        {
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X64) };
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X86) };
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Native_E2e_NativeInt(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(Program, "nint-native", (TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal(Expected, stdout);
        }

        private static (int ExitCode, string Stdout) EmitNativeAndRun(string source, string name, TargetPlatform platform)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-nint-native");
            Directory.CreateDirectory(directory);
            var suffix = platform.Arch == Architecture.X86 ? "-x86" : "";
            var exePath = Path.Combine(directory, name + suffix + ".exe");
            var diagnostics = compilation.EmitNative(name, exePath, platform);

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

        // ---- .coa round-trip（@nint/@nuint TypeRef） -------------------------

        [Fact]
        public void Cod_RoundTrip_NativeInt_FieldAndParam()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-nint-cod-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            var libSource = @"namespace MyLib
{
    class Box
    {
        public field Raw: nint

        public constructor(value: nint)
        {
            Raw = value
        }

        public function IsNull(): bool
        {
            return Raw == 0
        }

        public function ToLong(): i64
        {
            return Raw
        }
    }
}";
            var libTree = SyntaxTree.Parse(libSource);
            var lib = Compilation.Create(libTree);
            var output = Path.Combine(dir, "Box.coa");
            var emitDiagnostics = lib.EmitCocoa("Box", output);

            Assert.True(emitDiagnostics.IsEmpty, string.Join("\n", emitDiagnostics.Select(d => d.Message)));
            var text = File.ReadAllText(output);
            Assert.Contains("@nint", text);

            var consumer = @"using System
using MyLib

function Main(): i32
{
    let b = new Box(0)
    Console.WriteLine(b.IsNull())
    Console.WriteLine(b.ToLong() == 0)
    return 0
}";
            var consumerTree = SyntaxTree.Parse(consumer);
            var compilation = Compilation.Create("Main", new[] { output, typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, consumerTree);

            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var eval = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!eval.Diagnostics.HasErrors(), string.Join("\n", eval.Diagnostics.Select(d => d.Message)));
                Assert.Equal(CodExpected, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }
    }
}