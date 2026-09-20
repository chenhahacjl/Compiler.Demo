using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// object 多态（装箱/拆箱/is/as 值类型）——Evaluator 后端差分验证。
    /// `object` 类型解析为 System.Object：值类型/string/类隐式装箱，`(T)obj` 显式拆箱，
    /// `obj is T` / `obj as T` 对装箱值做运行时类型判定。
    /// </summary>
    public class ObjectPolymorphismTests
    {
        private static object? Run(string source, out string output)
        {
            var original = Console.Out;
            try
            {
                using var writer = new System.IO.StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create(SyntaxTree.Parse(source));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                output = writer.ToString();
                return result.Value;
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void Boxing_Int_ToObject_And_Unbox()
        {
            var source =
                "function Main(): i32\n{\n" +
                "    let o: object = 5\n" +
                "    let back: i32 = (i32)o\n" +
                "    return back\n" +
                "}\n";
            var value = Run(source, out _);
            Assert.Equal(5, Assert.IsType<int>(value));
        }

        [Fact]
        public void Boxing_Double_String_Bool_ToObject()
        {
            var source =
                "function Main(): i32\n{\n" +
                "    let a: object = 3.14\n" +
                "    let b: object = \"hi\"\n" +
                "    let c: object = true\n" +
                "    let da: f64 = (f64)a\n" +
                "    let sb: string = (string)b\n" +
                "    let cb: bool = (bool)c\n" +
                "    if da == 3.14 && sb == \"hi\" && cb == true\n" +
                "    {\n" +
                "        return 1\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n";
            var value = Run(source, out _);
            Assert.Equal(1, Assert.IsType<int>(value));
        }

        [Fact]
        public void IsExpression_On_Boxed_Values()
        {
            var source =
                "function Main(): i32\n{\n" +
                "    let a: object = 5\n" +
                "    let b: object = \"hi\"\n" +
                "    let c: object = 3.5\n" +
                "    var n = 0\n" +
                "    if a is i32 { n = n + 1 }\n" +
                "    if !(a is string) { n = n + 1 }\n" +
                "    if b is string { n = n + 1 }\n" +
                "    if !(b is i32) { n = n + 1 }\n" +
                "    if c is f64 { n = n + 1 }\n" +
                "    return n\n" +
                "}\n";
            var value = Run(source, out _);
            Assert.Equal(5, Assert.IsType<int>(value));
        }

        [Fact]
        public void AsExpression_On_Boxed_Values()
        {
            var source =
                "function Main(): i32\n{\n" +
                "    let a: object = 5\n" +
                "    let b: object = \"hi\"\n" +
                "    let c: object = 3.5\n" +
                "    let ai = a as i32\n" +
                "    let bs = b as string\n" +
                "    let cd = c as f64\n" +
                "    var r = ai\n" +
                "    if bs == \"hi\" { r = r + 1 }\n" +
                "    if cd == 3.5 { r = r + 1 }\n" +
                "    return r\n" +
                "}\n";
            var value = Run(source, out _);
            Assert.Equal(7, Assert.IsType<int>(value));
        }

        [Fact]
        public void Object_As_Return_And_Param()
        {
            var source =
                "function Wrap(v: i32): object\n{\n    return v\n}\n\n" +
                "function Unwrap(o: object): i32\n{\n    return (i32)o\n}\n\n" +
                "function Main(): i32\n{\n" +
                "    let o: object = Wrap(42)\n" +
                "    return Unwrap(o)\n" +
                "}\n";
            var value = Run(source, out _);
            Assert.Equal(42, Assert.IsType<int>(value));
        }

        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        [Theory]
        [InlineData(
            "function Main(): i32\n{\n" +
            "    let a: object = 5\n" +
            "    let b: object = \"hi\"\n" +
            "    let c: object = 3.5\n" +
            "    if a is i32 && b is string && c is f64 { return (i32)a }\n" +
            "    return 0\n" +
            "}\n", 5)]
        [InlineData(
            "function Main(): i32\n{\n" +
            "    let a: object = 5\n" +
            "    if a is string { return 1 }\n" +
            "    return 0\n" +
            "}\n", 0)]
        [InlineData(
            "function Main(): i32\n{\n" +
            "    let b: object = \"hi\"\n" +
            "    if b is string { return 1 }\n" +
            "    return 0\n" +
            "}\n", 1)]
        [InlineData(
            "function Main(): i32\n{\n" +
            "    let o: object = 7\n" +
            "    return (i32)o\n" +
            "}\n", 7)]
        public void Il_BoxingAndIsAs_Match(string source, int expectedExit)
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(source));
            var exePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cocoa-obj-il-" + Guid.NewGuid().ToString("N") + ".exe");
            var diagnostics = compilation.Emit("ml", References(), exePath, Cocoa.Targeting.IlTarget.Parse("net9.0"));
            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));

            var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{exePath}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);
            Assert.True(expectedExit == process.ExitCode, $"expected {expectedExit}, got {process.ExitCode}");
        }

        [Theory]
        [InlineData(
            "function Main(): i32\n{\n" +
            "    let a: object = 5\n" +
            "    let b: object = \"hi\"\n" +
            "    if a is i32 { return (i32)a }\n" +
            "    return 0\n" +
            "}\n", 5)]
        [InlineData(
            "function Main(): i32\n{\n" +
            "    let a: object = 5\n" +
            "    if a is string { return 1 }\n" +
            "    return 0\n" +
            "}\n", 0)]
        [InlineData(
            "function Main(): i32\n{\n" +
            "    let o: object = 7\n" +
            "    return (i32)o\n" +
            "}\n", 7)]
        [InlineData(
            "function Main(): i32\n{\n" +
            "    let c: object = 3.5\n" +
            "    if c is f64 { return 1 }\n" +
            "    return 0\n" +
            "}\n", 1)]
        public void Native_BoxingAndIsAs_Match(string source, int expectedExit)
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(source));
            var exePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cocoa-obj-native-" + Guid.NewGuid().ToString("N") + ".exe");
            var diagnostics = compilation.EmitNative("ml", exePath, new Cocoa.Targeting.TargetPlatform(Cocoa.Targeting.TargetOS.Windows, Cocoa.Targeting.Architecture.X64));
            Assert.True(diagnostics.IsEmpty, "native emit diagnostics: " + string.Join("\n", diagnostics.Select(d => d.Message)));

            var psi = new System.Diagnostics.ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);
            Assert.True(expectedExit == process.ExitCode, $"expected {expectedExit}, got {process.ExitCode}");
        }
    }
}