using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeGen.Native;
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
    /// System.IO 文本层三后端锁定（8a）：StreamWriter/StreamReader/StringWriter/StringReader
    /// 纯 Cocoa body（UTF-8 经 Runtime.String* 原语、CRLF 行尾/Line 切断）。Evaluator / IL / native ×（x64/x86）。
    /// </summary>
    public class StreamTextThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Program = @"using System
using System.IO

function Main(): i32
{
    let ms = new MemoryStream()
    let w = new StreamWriter(ms)
    w.WriteLine(""alpha"")
    w.Write(""be"")
    w.WriteLine(""ta"")
    w.WriteLineEmpty()
    ms.Position = 0
    let r = new StreamReader(ms)
    System.Console.WriteLine(r.ReadLine() == ""alpha"")
    System.Console.WriteLine(r.ReadLine() == ""beta"")
    System.Console.WriteLine(r.ReadLine() == """")
    System.Console.WriteLine(r.ReadLine() == """")

    let c = new char[6]
    c[0] = 'l'
    c[1] = '1'
    c[2] = '\r'
    c[3] = '\n'
    c[4] = 'l'
    c[5] = '2'
    let text = Runtime.StringFromChars(c)
    let sr = new StringReader(text)
    System.Console.WriteLine(sr.ReadLine() == ""l1"")
    System.Console.WriteLine(sr.ReadLine() == ""l2"")
    System.Console.WriteLine(sr.ReadLine() == """")

    let sw = new StringWriter()
    sw.Write(""hi"")
    System.Console.WriteLine(sw.GetValue() == ""hi"")
    sw.WriteLine(""!"")
    System.Console.WriteLine(sw.GetValue().Length == 5)
    return 0
}";

        private const string Expected = "True\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\n";

        [Fact]
        public void Evaluator_StreamText()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                var actual = writer.ToString().Replace("\r\n", "\n");
                Assert.True(actual == Expected, "DIAG:\n" + string.Join("\n", result.Diagnostics.Select(d => d.Message)) + "\nGOT:\n[" + actual + "]");
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void IlE2e_StreamText()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-streamtext", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "std.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var diagnostics = compilation.Emit("stdtext", References(), exePath, IlTarget.Parse("net9.0"));
            Assert.Empty(string.Join("\n", diagnostics));

            var psi = new ProcessStartInfo("dotnet", $"\"{exePath}\"") { RedirectStandardOutput = true, UseShellExecute = false };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            Assert.True(process.WaitForExit(20000));
            outputTask.Wait();
            var stdout = Encoding.UTF8.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(Expected, stdout);
        }

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_StreamText(string target)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-streamtext", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "std-" + target + ".exe");
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var diagnostics = compilation.EmitNative("stdtext", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(Expected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
        }
    }
}