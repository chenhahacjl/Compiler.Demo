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
    /// 主构造函数（C# 12）：`class Point(x: i32, y: i32)`——位置参数在 parser 层展开为
    /// **私有捕获字段** + 隐式构造器（`this.x = x`）。类体内裸名 `x` 可读（成员解析落捕获字段），
    /// 外部 `point.x` 被可见性拦截（C#「参数仅构造器捕获」语义）。三后端同 records 的字段+构造器路径。
    /// </summary>
    public class PrimaryConstructorTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Program = @"using System

public class Point(x: i32, y: i32)
{
    public function Sum(): i32
    {
        return x + y
    }

    public function Scaled(s: i32): i32
    {
        return (x + y) * s
    }

    public function Swap(): Point
    {
        return new Point(y, x)
    }
}

function Main(): i32
{
    var p = new Point(3, 4)
    Console.WriteLine(p.Sum())
    Console.WriteLine(p.Scaled(2))
    var q = p.Swap()
    Console.WriteLine(q.Sum())
    return 0
}";

        private const string Expected = "7\n14\n7\n";

        // ---- Evaluator ---------------------------------------------------------

        [Fact]
        public void Evaluator_PrimaryCtor_CapturedParams()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(Expected, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        // ---- IL ---------------------------------------------------------------

        [Fact]
        public void IlE2e_PrimaryCtor()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-prim-e2e", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "t.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
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
            Assert.Equal(Expected, stdout);
        }

        // ---- Native -----------------------------------------------------------

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_PrimaryCtor(string target)
        {
            TargetPlatform.TryParse(target, out var platform);
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-prim-native-e2e", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "t-" + target + ".exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var diagnostics = compilation.EmitNative("t", exePath, platform);
            Assert.True(!diagnostics.HasErrors(), string.Join("\n", diagnostics.Select(d => d.Message)));

            var psi = new ProcessStartInfo(exePath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            using var errReader = process.StandardError;
            var errTask = errReader.ReadToEndAsync();
            Assert.True(process.WaitForExit(20000));
            outputTask.Wait();
            var stdout = Encoding.Unicode.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            Assert.True(process.ExitCode == 0, "exit=" + process.ExitCode + " stderr=" + errTask.Result.Replace("\n", " | "));
            Assert.Equal(Expected, stdout);
        }

        // ---- 绑定语义 ----------------------------------------------------------

        [Fact]
        public void Bind_ExternalAccessToCapturedParam_Blocked()
        {
            // C#「参数仅构造器捕获」：point.x 不可经外部访问（私有捕获字段）
            var diagnostics = Compilation.Create(SyntaxTree.Parse(@"using System

public class Point(x: i32, y: i32)
{
    public function Sum(): i32 { return x + y }
}

function Main(): i32
{
    var p = new Point(1, 2)
    Console.WriteLine(p.x)
    return 0
}")).GetDiagnostics();
            Assert.Contains(diagnostics, d => d.IsError && d.Message.Contains("private"));
        }

        [Fact]
        public void Bind_ExplicitInstanceCtor_Conflict()
        {
            var diagnostics = Compilation.Create(SyntaxTree.Parse(@"using System

public class Point(x: i32, y: i32)
{
    public constructor(a: i32) { }
    public function Sum(): i32 { return x + y }
}

function Main(): i32 { return 0 }")).GetDiagnostics();
            Assert.Contains(diagnostics, d => d.IsError && d.Message.Contains("主构造函数参数与显式实例构造器"));
        }

        [Fact]
        public void Bind_DuplicateCapturedField()
        {
            var diagnostics = Compilation.Create(SyntaxTree.Parse(@"using System

public class Point(x: i32, y: i32)
{
    private field x: i32
    public function Sum(): i32 { return x + y }
}

function Main(): i32 { return 0 }")).GetDiagnostics();
            Assert.Contains(diagnostics, d => d.IsError && d.Message.Contains("already declared"));
        }
    }
}