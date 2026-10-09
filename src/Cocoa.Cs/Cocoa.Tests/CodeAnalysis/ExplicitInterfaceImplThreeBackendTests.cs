using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.Targeting;
using Cocoa.CodeGen.Native;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 显式接口实现（`function IShape.Area(): i32`）：显式方法以限定名注册，仅经接口接收者分派。
    /// 三后端：Evaluator（ResolveDispatch 显式匹配）、IL（MethodImpl 表重定向，CLR callvirt 解析）、
    /// native（B3 接口 vtable 槽位经 FindImplementation 显式匹配）。
    /// </summary>
    public class ExplicitInterfaceImplThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string ExplicitImplProgram = @"using System

public interface IShape
{
    function Area(): i32
    function Perimeter(): i32
}

public class Rect extends IShape
{
    private field _w: i32
    private field _h: i32

    public constructor(w: i32, h: i32)
    {
        _w = w
        _h = h
    }

    public function IShape.Area(): i32
    {
        return _w * _h
    }

    public function IShape.Perimeter(): i32
    {
        return 2 * (_w + _h)
    }
}

function ShapeArea(s: IShape): i32
{
    return s.Area()
}

function Main(): i32
{
    var s: IShape = new Rect(3, 4)
    Console.WriteLine(s.Area())
    Console.WriteLine(s.Perimeter())
    Console.WriteLine(ShapeArea(s))
    Console.WriteLine(((IShape)new Rect(5, 6)).Area())
    return 0
}";

        private const string Expected = "12\n14\n12\n30\n";

        // ---- Evaluator ---------------------------------------------------------

        [Fact]
        public void Evaluator_ExplicitInterfaceImpl_Dispatch()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(ExplicitImplProgram));
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
        public void IlE2e_ExplicitInterfaceImpl_Dispatch()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-exi-il-e2e", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "t.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(ExplicitImplProgram));
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

        [Fact]
        public void IlE2e_FacadeInterface_ExplicitImpl()
        {
            // facade 接口（[Facade("System.IDisposable")]）：MethodImpl 的 MethodDeclaration 走 MemberRef（BCL 接口成员）
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-exi-facade-e2e", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "t.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(@"using System

[Facade(""System.IDisposable"")] public interface IDisposable
{
    public function Dispose(): void
}

public class Resource extends IDisposable
{
    private field _used: bool

    public function IDisposable.Dispose(): void
    {
        _used = true
    }

    public function WasUsed(): bool
    {
        return _used
    }
}

function Main(): i32
{
    var r = new Resource()
    var d: IDisposable = r
    d.Dispose()
    Console.WriteLine(r.WasUsed())
    return 0
}"));
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
            Assert.Equal("True\n", stdout);
        }

        // ---- Native -----------------------------------------------------------

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_ExplicitInterfaceImpl_Dispatch(string target)
        {
            TargetPlatform.TryParse(target, out var platform);
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-exi-native-e2e", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "t-" + target + ".exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(ExplicitImplProgram));
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
        public void Evaluator_Unqualified_InterfaceMemberDispatch()
        {
            // Cocoa 语义：类接收者的接口成员查找可命中实现的接口方法（GetMethod 沿 _interfaces 扫描），
            // 显式实现经虚分派落到显式方法——与 C# 的「仅接口可达」不同，本语言放宽为同样可用。
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(@"using System

public interface IShape
{
    function Area(): i32
}

public class Rect extends IShape
{
    private field _w: i32

    public constructor(w: i32)
    {
        _w = w
    }

    public function IShape.Area(): i32
    {
        return _w * 2
    }
}

function Main(): i32
{
    var r = new Rect(6)
    Console.WriteLine(r.Area())
    return 0
}"));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal("12\n", writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void Bind_ClassNotImplementing_Rejected()
        {
            var diagnostics = Compilation.Create(SyntaxTree.Parse(@"using System

public interface IReader { function Read(): i32 }

public class Doc
{
    public function IReader.Read(): i32 { return 1 }
}

function Main(): i32 { return 0 }")).GetDiagnostics();
            Assert.Contains(diagnostics, d => d.IsError && d.Message.Contains("未实现接口"));
        }

        [Fact]
        public void Bind_UnknownInterfaceMember_Rejected()
        {
            var diagnostics = Compilation.Create(SyntaxTree.Parse(@"using System

public interface IReader { function Read(): i32 }

public class Doc extends IReader
{
    public function IReader.Missing(): i32 { return 1 }
}

function Main(): i32 { return 0 }")).GetDiagnostics();
            Assert.Contains(diagnostics, d => d.IsError && d.Message.Contains("没有成员"));
        }

        // ---- 基类显式实现 + 派生继承 -------------------------------------------

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_InheritedExplicitImpl(string target)
        {
            TargetPlatform.TryParse(target, out var platform);
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-exi-inherit-e2e", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "t-" + target + ".exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(@"using System

public interface IGreeter
{
    function Greet(): string
}

public class BaseGreeter extends IGreeter
{
    private field _who: string

    public constructor(who: string)
    {
        _who = who
    }

    public function IGreeter.Greet(): string
    {
        return ""Hi "" + _who
    }
}

public class LoudGreeter extends BaseGreeter
{
    public constructor(who: string) extends base(who)
    {
    }
}

function Echo(g: IGreeter): string
{
    return g.Greet()
}

function Main(): i32
{
    var b: IGreeter = new BaseGreeter(""Bob"")
    var l: IGreeter = new LoudGreeter(""Ann"")
    Console.WriteLine(b.Greet())
    Console.WriteLine(Echo(l))
    return 0
}"));
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
            Assert.Equal("Hi Bob\nHi Ann\n", stdout);
        }
    }
}