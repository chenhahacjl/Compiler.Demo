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
    /// with 表达式（C# 9 record 非破坏复制）：`p with { 字段 = 值, ... }`
    /// 脱糖为位置参数构造器调用（未覆盖字段原值透传），三后端零新发射代码。
    /// </summary>
    public class WithExpressionThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Template = @"using System

record Point(x: i32, y: i32)

function Main(): i32
{
    var p1 = new Point(1, 2)
    var p2 = p1 with { x = 3 }
    System.Console.WriteLine(p2.x)
    System.Console.WriteLine(p2.y)
    System.Console.WriteLine(p1.x)
    System.Console.WriteLine(p1.y)
    var p3 = p1 with { x = 10, y = 20 }
    System.Console.WriteLine(p3.x)
    System.Console.WriteLine(p3.y)
    return 0
}";

        private const string Expected = "3\n2\n1\n2\n10\n20\n";

        [Fact]
        public void Evaluator_WithExpression()
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
        public void IlE2e_WithExpression()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-with", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "with.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.Emit("withtst", References(), exePath, IlTarget.Parse("net9.0"));
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
        public void NativeE2e_WithExpression(string target)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-with", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "with-" + target + ".exe");
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.EmitNative("withsdk", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(Expected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
        }

        [Fact]
        public void WithExpression_BindsToObjectCreation_NotNewBoundNode()
        {
            // with 脱糖为位置参数构造器调用：绑定树出现 ObjectCreationExpression，无 WithExpression 泄漏
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            Assert.True(!compilation.GetDiagnostics().HasErrors());
            var program = compilation.GetProgram();
            var mainBody = program.Functions.First(f => f.Key.Name == "Main").Value;
            var sawObjectCreation = false;
            Visit(mainBody, n =>
            {
                if (n.Kind == Cocoa.CodeAnalysis.Binding.BoundNodeKind.ObjectCreationExpression)
                    sawObjectCreation = true;
            });
            Assert.True(sawObjectCreation);
        }

        [Fact]
        public void WithExpression_NonPositionalTarget_ReportsError()
        {
            var source = @"using System
record Point(x: i32, y: i32)

function Main(): i32
{
    var p1 = new Point(1, 2)
    var p2 = p1 with { z = 3 }
    return 0
}";
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
            var diagnostics = compilation.GetDiagnostics();
            Assert.True(diagnostics.HasErrors());
            Assert.True(diagnostics.Any(d => d.Message.Contains("不是 record 'Point' 的位置参数字段")));
        }

        private static void Visit(BoundNode node, Action<BoundNode> action)
        {
            action(node);
            foreach (var child in Compilation.BoundChildren(node))
            {
                if (child != null)
                    Visit(child, action);
            }
        }
    }
}
