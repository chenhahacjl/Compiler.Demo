using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Binding;
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
using System.Linq;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// file 文件范围类型（C# 11）：`file class Foo` 仅本源码文件可见，跨文件类型解析视为未定义。
    /// </summary>
    public class FileScopedTypeThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Template = @"using System

file class Internal
{
    public function Value(): i32
    {
        return 7
    }
}

function Main(): i32
{
    var i = new Internal()
    System.Console.WriteLine(i.Value())
    return 0
}";

        private const string Expected = "7\n";

        [Fact]
        public void Evaluator_FileScopedType_SameFile()
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
        public void IlE2e_FileScopedType_SameFile()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-filetype", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "filetype.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.Emit("fttst", References(), exePath, IlTarget.Parse("net9.0"));
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
        public void NativeE2e_FileScopedType_SameFile(string target)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-filetype", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "filetype-" + target + ".exe");
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.EmitNative("ftsdk", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(Expected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
        }

        [Fact]
        public void FileScopedType_CrossFile_NotVisible()
        {
            // 树 A：声明 file class Hidden；树 B：引用 Hidden → 应为未定义错误
            var treeA = SyntaxTree.Parse(@"
file class Hidden
{
    public function X(): i32 { return 1 }
}");
            var treeB = SyntaxTree.Parse(@"using System

function Main(): i32
{
    var h = new Hidden()
    return 0
}");
            var compilation = Compilation.Create("Main", References(), treeA, treeB);
            var diagnostics = compilation.GetDiagnostics();
            Assert.True(diagnostics.HasErrors());
            Assert.True(diagnostics.Any(d => d.Message.Contains("Hidden")));
        }

        [Fact]
        public void FileScopedType_IsMarked()
        {
            var tree = SyntaxTree.Parse(Template);
            var compilation = Compilation.Create("Main", References(), tree);
            var program = compilation.GetProgram();
            var internalType = compilation.GlobalScope.Classes.FirstOrDefault(t => t.Name == "Internal");
            Assert.NotNull(internalType);
            Assert.True(internalType!.IsFileScoped);
        }
    }
}