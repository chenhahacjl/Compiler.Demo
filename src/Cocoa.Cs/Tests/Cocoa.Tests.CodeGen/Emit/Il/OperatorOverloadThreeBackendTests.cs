using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.CodeGen.Emit.Il
{
    /// <summary>
    /// 运算符重载三后端封板（4.5）：声明 → 绑定回落（OperatorRegistry）→ 三后端发射，
    /// Evaluator / IL / native ×（x64/x86）输出断言一致。覆盖二元、复合赋值、比较、
    /// 一元、隐式/显式转换五类。IL 侧另有 <c>OperatorOverloadProbe</c> 的白盒（op_* 元数据名）。
    /// </summary>
    public class OperatorOverloadThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string BinaryProgram = @"using System

class Vec
{
    public field X: i32

    public static function operator +(a: Vec, b: Vec): Vec
    {
        var r = new Vec()
        r.X = a.X + b.X
        return r
    }

    public static function operator *(a: Vec, b: Vec): Vec
    {
        var r = new Vec()
        r.X = a.X * b.X
        return r
    }

    public static function operator -(a: Vec, b: Vec): Vec
    {
        var r = new Vec()
        r.X = a.X - b.X
        return r
    }
}

function Main(): i32
{
    var a = new Vec()
    a.X = 1
    var b = new Vec()
    b.X = 2

    var add = a + b
    Console.WriteLine(add.X)

    var mul = a * b
    Console.WriteLine(mul.X)

    var ten = new Vec()
    ten.X = 10
    var sub = ten - b
    Console.WriteLine(sub.X)
    return 0
}";

        private const string BinaryExpected = "3\n2\n8\n";

        private const string CompoundComparisonProgram = @"using System

class Vec
{
    public field X: i32

    public static function operator +(a: Vec, b: Vec): Vec
    {
        var r = new Vec()
        r.X = a.X + b.X
        return r
    }

    public static function operator ==(a: Vec, b: Vec): bool
    {
        return a.X == b.X
    }

    public static function operator -(a: Vec, b: Vec): Vec
    {
        var r = new Vec()
        r.X = a.X - b.X
        return r
    }
}

function Main(): i32
{
    var c = new Vec()
    c.X = 4
    var b = new Vec()
    b.X = 2

    c += b
    Console.WriteLine(c.X)

    var d = new Vec()
    d.X = 10
    d -= b
    Console.WriteLine(d.X)

    var same = new Vec()
    same.X = 2
    Console.WriteLine(b == same)
    Console.WriteLine(b == d)
    return 0
}";

        private const string CompoundComparisonExpected = "6\n8\nTrue\nFalse\n";

        private const string UnaryProgram = @"using System

class Vec
{
    public field X: i32

    public static function operator -(a: Vec): Vec
    {
        var r = new Vec()
        r.X = 0 - a.X
        return r
    }
}

function Main(): i32
{
    var a = new Vec()
    a.X = 5
    var neg = -a
    Console.WriteLine(neg.X)
    return 0
}";

        private const string UnaryExpected = "-5\n";

        private const string ConversionProgram = @"using System

class Vec
{
    public field X: i32

    public static function implicit operator int(v: Vec): i32
    {
        return v.X
    }

    public static function explicit operator Vec(v: i32): Vec
    {
        var r = new Vec()
        r.X = v
        return r
    }
}

function Main(): i32
{
    var a = new Vec()
    a.X = 9
    var s: i32 = a
    Console.WriteLine(s + 1)

    var b: Vec = (Vec)5
    Console.WriteLine(b.X)
    return 0
}";

        private const string ConversionExpected = "10\n5\n";

        public static IEnumerable<object[]> GetBackends()
        {
            yield return new object[] { "evaluator" };
            yield return new object[] { "native-x64" };
            yield return new object[] { "native-x86" };
            yield return new object[] { "il" };
        }

        [Theory]
        [MemberData(nameof(GetBackends))]
        public void BinaryOperators_ThreeBackends(string backend)
        {
            RunBackend(BinaryProgram, BinaryExpected, "OpBinary3", backend);
        }

        [Theory]
        [MemberData(nameof(GetBackends))]
        public void CompoundAssignmentAndComparison_ThreeBackends(string backend)
        {
            RunBackend(CompoundComparisonProgram, CompoundComparisonExpected, "OpCompound3", backend);
        }

        [Theory]
        [MemberData(nameof(GetBackends))]
        public void UnaryOperator_ThreeBackends(string backend)
        {
            RunBackend(UnaryProgram, UnaryExpected, "OpUnary3", backend);
        }

        [Theory]
        [MemberData(nameof(GetBackends))]
        public void ImplicitExplicitConversion_ThreeBackends(string backend)
        {
            RunBackend(ConversionProgram, ConversionExpected, "OpConversion3", backend);
        }

        private static void RunBackend(string source, string expected, string name, string backend)
        {
            if (backend == "evaluator")
            {
                var original = Console.Out;
                try
                {
                    using var writer = new StringWriter();
                    Console.SetOut(writer);
                    var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
                    var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                    Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                    Assert.Equal(expected, writer.ToString().Replace("\r\n", "\n"));
                }
                finally
                {
                    Console.SetOut(original);
                }

                return;
            }

            if (backend == "il")
            {
                var (exitCode, stdout) = IlEndToEndTests.EmitAndRun(source, name);
                Assert.Equal(0, exitCode);
                Assert.Equal(expected, stdout.Replace("\r\n", "\n"));
                return;
            }

            var platform = backend == "native-x86"
                ? new TargetPlatform(TargetOS.Windows, Architecture.X86)
                : new TargetPlatform(TargetOS.Windows, Architecture.X64);
            var tree = SyntaxTree.Parse(source);
            var compilationNative = Compilation.Create("Main", References(), tree);
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-op3-tests");
            Directory.CreateDirectory(directory);
            var exePath = Path.Combine(directory, name + (platform.Arch == Architecture.X86 ? "-x86" : "-x64") + ".exe");
            var diagnostics = compilationNative.EmitNative(name, exePath, platform);

            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));
            Assert.True(File.Exists(exePath));

            var psi = new System.Diagnostics.ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            using var output = new System.IO.MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);

            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new TimeoutException("Native exe did not exit in time.");
            }

            outputTask.Wait();
            var nativeStdout = Encoding.Unicode.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(expected, nativeStdout);
        }
    }
}
