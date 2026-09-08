using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.Targeting;
using Cocoa.CodeGen.Native;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Tests.CodeAnalysis.Emit.Native;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>C1：泛型 × byref —— `Resize&lt;T&gt;(ref a: T[], newSize)`（Array.Resize 语义）三端。</summary>
    public class GenericByRefResizeThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Template = @"using System

public class ArrayX
{
    public static function Resize<T>(ref a: T[], newSize: i32): void
    {
        var b = new T[newSize]
        var copyCount = newSize < a.Length ? newSize : a.Length
        var i = 0
        while i < copyCount
        {
            b[i] = a[i]
            i = i + 1
        }
        a = b
    }
}

function Main(): i32
{
    var arr = new i32[3]
    arr[0] = 10
    arr[1] = 20
    arr[2] = 30
    ArrayX.Resize<i32>(ref arr, 5)
    System.Console.WriteLine(arr.Length == 5)
    arr[3] = 40
    System.Console.WriteLine(arr[3] == 40)
    System.Console.WriteLine(arr[2] == 30)
    var names = new string[2]
    names[0] = ""a""
    names[1] = ""b""
    ArrayX.Resize<string>(ref names, 1)
    System.Console.WriteLine(names.Length == 1)
    System.Console.WriteLine(names[0] == ""a"")
    return 0
}";

        private const string Expected = "True\nTrue\nTrue\nTrue\nTrue\n";

        [Fact]
        public void Evaluator_GenericByRefResize()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(Expected, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void IlE2e_GenericByRefResize()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-gref", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "gref.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.Emit("gref", References(), exePath, IlTarget.Parse("net9.0"));
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
            Assert.Equal(Expected, stdout);
        }

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_GenericByRefResize(string target)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-gref", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "gref-" + target + ".exe");
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.EmitNative("gref", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(Expected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
        }
    }
}