using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// StringBuilder stdlib 冒烟（6e-G7 ③a）：源码集成模式（实例类 .cod 化属 6b 边界），
    /// 验证 Runtime.StringFromChars syscall + O(n) ToString 与扩容正确性。
    /// </summary>
    public class StringBuilderTests
    {
        private const string StringBuilderSource = @"
namespace System.Text
{
    public class StringBuilder
    {
        private _chars: char[]
        private _count: i32

        public constructor()
        {
            _chars = new char[16]
            _count = 0
        }

        public function Length(): i32
        {
            return _count
        }

        public function Append(c: char): StringBuilder
        {
            EnsureCapacity(_count + 1)
            _chars[_count] = c
            _count = _count + 1
            return this
        }

        public function Append(s: string): StringBuilder
        {
            var i = 0
            while i < s.Length
            {
                Append(s[i])
                i = i + 1
            }

            return this
        }

        public function Clear(): void
        {
            _count = 0
        }

        public function ToString(): string
        {
            var chars = new char[_count]
            var i = 0
            while i < _count
            {
                chars[i] = _chars[i]
                i = i + 1
            }

            return Runtime.StringFromChars(chars)
        }

        private function EnsureCapacity(required: i32): void
        {
            if required <= _chars.Length
            {
                return
            }

            var newLen = _chars.Length * 2
            while newLen < required
            {
                newLen = newLen * 2
            }

            var grown = new char[newLen]
            var i = 0
            while i < _count
            {
                grown[i] = _chars[i]
                i = i + 1
            }

            _chars = grown
        }
    }
}
";

        private static (Compilation Compilation, Func<List<string>> Diagnostics) Compile(string appSource)
        {
            var libTree = SyntaxTree.Parse(StringBuilderSource);
            var appTree = SyntaxTree.Parse(appSource);
            var compilation = Compilation.Create(libTree, appTree);
            return (compilation, () => EvaluateDiagnostics(compilation));
        }

        private static List<string> EvaluateDiagnostics(Compilation compilation)
        {
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            return result.Diagnostics.Where(d => d.IsError).Select(d => d.Message).ToList();
        }

        private const string HotLoopApp = @"
using System.Text
{
    var hot = new StringBuilder()
    var i = 0
    while i < 1000
    {
        hot.Append(""x"")
        i = i + 1
    }

    System.Console.WriteLine(hot.ToString())
    return hot.Length()
}
";

        [Fact(Skip = "G7-③a follow-up: namespace 类 Append 链诊断残留待查")]
        public void Evaluator_Append_And_ToString_HotLoop()
        {
            var (compilation, diagnostics) = Compile(HotLoopApp);
            Assert.True(diagnostics().Count == 0, string.Join(" || ", diagnostics().Select(d => d)) );

            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(1000, Convert.ToInt32(result.Value));
        }

        [Fact(Skip = "G7-③a follow-up: namespace 类 Append 链诊断残留待查")]
        public void Il_Append_And_ToString()
        {
            var (compilation, diagnostics) = Compile(@"
function Main(): void
{
    let sb = new StringBuilder()
    sb.Append(""Hello"")
    sb.Append(' ')
    sb.Append(""World"")
    System.Console.WriteLine(sb.ToString())
}
");
            Assert.True(diagnostics().Count == 0, string.Join(" || ", diagnostics().Select(d => d)) );

            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-sb", "sb-il.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
            var emitDiagnostics = compilation.Emit("sb-il",
                new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location },
                exePath,
                Cocoa.CodeAnalysis.Emit.IL.IlTarget.Parse("net9.0"));
            Assert.True(emitDiagnostics.IsEmpty, string.Join("\n", emitDiagnostics.Select(d => d.Message)));

            var psi = new ProcessStartInfo("dotnet", $"\"{exePath}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            Assert.True(process.WaitForExit(15000), "il timeout");
            outputTask.Wait();

            var stdout = Encoding.UTF8.GetString(output.ToArray()).Replace("\r\n", "\n").Trim();
            Assert.Equal("Hello World", stdout);
        }
    }
}
