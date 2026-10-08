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
    /// global using（C# 10）：`global using System`——编译范围生效。
    /// 本编译器全局作用域已跨文件聚合 using，语义等价普通顶层 using；本测试锁定语法接受 + 跨域可用。
    /// </summary>
    public class GlobalUsingThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Template = @"global using System

function Main(): i32
{
    Console.WriteLine(""global using works"")
    return 0
}";

        private const string Expected = "global using works\n";

        [Fact]
        public void Evaluator_GlobalUsing()
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
        public void IlE2e_GlobalUsing()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-globalusing", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "globalusing.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.Emit("gutst", References(), exePath, IlTarget.Parse("net9.0"));
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
        public void NativeE2e_GlobalUsing(string target)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-globalusing", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "globalusing-" + target + ".exe");
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.EmitNative("gusdk", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(Expected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
        }

        [Fact]
        public void GlobalUsing_ParsesAsGlobalDirective()
        {
            var tree = SyntaxTree.Parse(Template);
            var usingDirective = tree.GetRootMembers().OfType<UsingDirectiveSyntax>().FirstOrDefault();
            Assert.NotNull(usingDirective);
            Assert.True(usingDirective!.IsGlobal);
        }
    }
}