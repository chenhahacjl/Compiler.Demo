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
    /// <summary>C2：record 解构——`(a, b) = person` 按位置参数字段序取值赋给变量。</summary>
    public class RecordDeconstructThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Template = @"using System

record Person(name: string, city: string)

function Main(): i32
{
    var a = """"
    var b = """"
    var p = new Person(""alice"", ""beijing"")
    (a, b) = p
    System.Console.WriteLine(a == ""alice"")
    System.Console.WriteLine(b == ""beijing"")
    var x = 0
    var y = """"
    var q = (7, ""yo"")
    (x, y) = q
    System.Console.WriteLine(x == 7)
    System.Console.WriteLine(y == ""yo"")
    return 0
}";

        private const string Expected = "True\nTrue\nTrue\nTrue\n";

        [Fact]
        public void Evaluator_RecordDeconstruct()
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
        public void IlE2e_RecordDeconstruct()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-rdec", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "rdec.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.Emit("rdec", References(), exePath, IlTarget.Parse("net9.0"));
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
        public void NativeE2e_RecordDeconstruct(string target)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-rdec", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "rdec-" + target + ".exe");
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.EmitNative("rdec", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(Expected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
        }
    }
}