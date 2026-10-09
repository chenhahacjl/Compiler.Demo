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
    var box: any = 42
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

    var neg: any = ""x""
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
    var box: any = 42
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
    }
}