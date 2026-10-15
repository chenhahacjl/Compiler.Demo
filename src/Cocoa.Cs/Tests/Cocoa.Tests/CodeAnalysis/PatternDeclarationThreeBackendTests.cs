using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;
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
    /// 声明模式（`expr is T var`）：值类型 / string / 类 / 接口目标三后端。
    /// Evaluator + IL 全目标支持（值类型走 boxed 判定/isinst+unbox.any）；native 引用（类/接口/继承）目标经
    /// vtable 链比对支持，值类型 / string 目标因 native `any` 无装箱运行时类型信息 → 编译期明确拒绝。
    /// </summary>
    public class PatternDeclarationThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string ValueStringProgram = @"using System

function Main(): i32
{
    var box: object = 42
    if box is i32 n
    {
        Console.WriteLine(n)
    }
    else
    {
        Console.WriteLine(""miss"")
    }

    var text: string = ""hi""
    if text is string t
    {
        Console.WriteLine(t.Length)
    }
    else
    {
        Console.WriteLine(""bad"")
    }

    var neg: object = ""x""
    if neg is f64 d
    {
        Console.WriteLine(d)
    }
    else
    {
        Console.WriteLine(""no-f64"")
    }

    return 0
}";

        private const string ValueStringExpected = "42\n2\nno-f64\n";

        // ---- Evaluator（值类型/string 全支持）-----------------------------------

        [Fact]
        public void Evaluator_ValueStringDeclarationPatterns()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(ValueStringProgram));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(ValueStringExpected, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        // ---- IL（值类型/string 全支持；Isinst/Unbox_Any 走 IsInstTypeToken）-----

        [Fact]
        public void IlE2e_ValueStringDeclarationPatterns()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-pat-il-e2e", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "t.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(ValueStringProgram));
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
            Assert.Equal(ValueStringExpected, stdout);
        }

        // ---- Native：引用目标（类/接口/继承）支持 ---------------------------------

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_ReferenceDeclarationPatterns(string target)
        {
            TargetPlatform.TryParse(target, out var platform);
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-pat-native-e2e", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "t-" + target + ".exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(@"using System

public class Animal
{
    public function Name(): string { return ""animal"" }
}

public class Dog extends Animal
{
}

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
        return _w
    }
}

function Main(): i32
{
    var a: Animal = new Dog()
    if a is Dog d
    {
        Console.WriteLine(""dog"")
    }

    var r = new Rect(6)
    if r is Rect rr
    {
        Console.WriteLine(rr.Area())
    }

    if r is IShape sh
    {
        Console.WriteLine(sh.Area())
    }
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
            Assert.Equal("dog\n6\n6\n", stdout);
        }

        // ---- Native：值类型 / string 目标编译期明确拒绝（无装箱运行时类型信息）-----

        [Theory]
        [InlineData("value-type", "box is i32 n", "private field none")]
        [InlineData("string", "text is string t", "private field none")]
        public void Native_ValueOrStringPattern_Rejected(string name, string pattern, string _)
        {
            var src = name == "string"
                ? @"using System
function Main(): i32
{
    var text: string = ""hi""
    if text is string t
    {
        Console.WriteLine(t.Length)
    }
    return 0
}"
                : @"using System
function Main(): i32
{
    var box: object = 42
    if box is i32 n
    {
        Console.WriteLine(n)
    }
    return 0
}";
            var compilation = Compilation.Create(SyntaxTree.Parse(src));
            var diagnostics = compilation.EmitNative("t", Path.Combine(Path.GetTempPath(), "cocoa-pat-rej-" + name + ".exe"), new TargetPlatform(TargetOS.Windows, Architecture.X64));
            Assert.Contains(diagnostics, d => d.IsError && d.Message.Contains("声明模式"));
        }

        // ------------------------------------------------------------------
        // 列表模式（C# 11）：expr is [1, 2, 3] / expr is [1, .., 3] / expr is [] / expr is [..]
        // 绑定降级为复合布尔（长度约束 + 逐元素相等），三后端零新增发射。
        // ------------------------------------------------------------------

        private const string ListPatternProgram = @"using System

function Main(): i32
{
    var a = [1, 2, 3]
    var empty: i32[] = []
    var r = 0
    if a is [1, 2, 3] { r = r + 1 }
    if a is [1, .., 3] { r = r + 2 }
    if a is [] { r = r + 4 }
    if empty is [] { r = r + 8 }
    if a is [1, ..] { r = r + 16 }
    if a is [..] { r = r + 32 }
    Console.WriteLine(r)
    return 0
}";

        private const string ListPatternExpected = "59\n";

        [Fact]
        public void Evaluator_ListPattern()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(ListPatternProgram));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(ListPatternExpected, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void IlE2e_ListPattern()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-pattern", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "listpat.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(ListPatternProgram));
            var diagnostics = compilation.Emit("listpat", References(), exePath, IlTarget.Parse("net9.0"));
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
            Assert.Equal(ListPatternExpected, stdout);
        }

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_ListPattern(string target)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-pattern", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "listpat-" + target + ".exe");
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(ListPatternProgram));
            var diagnostics = compilation.EmitNative("listpatsdk", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(ListPatternExpected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
        }

        // ------------------------------------------------------------------
        // 列表模式切片变量 `..rest`：匹配时把中段绑定为新数组（C# 11 slice designator）。
        // 实现走惰性三元（三端短路）+ CopyRange 合成调用，仅匹配时求值切片。
        // ------------------------------------------------------------------

        private const string ListRestProgram = @"using System

function Main(): i32
{
    var a = [1, 2, 3, 4, 5]
    var r = 0
    if a is [1, ..rest, 5] {
        if rest.Length == 3 { if rest[0] == 2 { if rest[2] == 4 { r = r + 1 } } }
    }
    if a is [1, ..middle] {
        if middle.Length == 4 { r = r + 2 }
    }
    if a is [..all] {
        if all.Length == 5 { r = r + 4 }
    }
    Console.WriteLine(r)
    return 0
}";

        private const string ListRestExpected = "7\n";

        [Fact]
        public void Evaluator_ListRest_SliceBinding()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(ListRestProgram));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(ListRestExpected, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void IlE2e_ListRest_SliceBinding()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-pattern", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "listrest.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(ListRestProgram));
            var diagnostics = compilation.Emit("listrest", References(), exePath, IlTarget.Parse("net9.0"));
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
            Assert.Equal(ListRestExpected, stdout);
        }

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_ListRest_SliceBinding(string target)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-pattern", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "listrest-" + target + ".exe");
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(ListRestProgram));
            var diagnostics = compilation.EmitNative("listrestsdk", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(ListRestExpected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
        }

        [Fact]
        public void Bind_ListRest_NonMatch_NoCrash()
        {
            // 长度不足时切片不应求值（惰性三元短路）：`[1, ..rest, 3]` 对 `[1]` 长度不足 → 整体 false，不越界。
            var source = @"using System

function Main(): i32
{
    var a = [1]
    if a is [1, ..rest, 3] { return 1 }
    return 0
}";
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.False(result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
            Assert.Equal(0, Assert.IsType<int>(result.Value));
        }
    }
}