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
    /// 集合表达式（C# 12）：`[1, 2, 3]` / `[..[10, 20], 4]`——绑定降级为数组创建，
    /// 元素类型从首个元素推导（空集合作 i32[]），`..` spread 仅支持编译期字面量数组展平。
    /// </summary>
    public class CollectionExpressionThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Template = @"using System

function Main(): i32
{
    var a = [1, 2, 3]
    System.Console.WriteLine(a.Length)
    System.Console.WriteLine(a[0])
    System.Console.WriteLine(a[2])
    var b = [..[10, 20], 4]
    System.Console.WriteLine(b.Length)
    System.Console.WriteLine(b[0])
    System.Console.WriteLine(b[2])
    var s = [""x"", ""y""]
    System.Console.WriteLine(s[1])
    return 0
}";

        private const string Expected = "3\n1\n3\n3\n10\n4\ny\n";

        [Fact]
        public void Evaluator_CollectionExpression()
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
        public void IlE2e_CollectionExpression()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-collection", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "collection.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.Emit("colltst", References(), exePath, IlTarget.Parse("net9.0"));
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
        public void NativeE2e_CollectionExpression(string target)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-collection", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "collection-" + target + ".exe");
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            var diagnostics = compilation.EmitNative("collsdk", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(Expected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
        }

        [Fact]
        public void CollectionExpression_BindsToArrayCreation()
        {
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Template));
            Assert.True(!compilation.GetDiagnostics().HasErrors());
            var program = compilation.GetProgram();
            var mainBody = program.Functions.First(f => f.Key.Name == "Main").Value;
            var sawArrayCreation = false;
            Visit(mainBody, n =>
            {
                if (n.Kind == BoundNodeKind.ArrayCreationExpression)
                    sawArrayCreation = true;
            });
            Assert.True(sawArrayCreation);
        }

        [Fact]
        public void CollectionExpression_VariableSpread_ReportsClearError()
        {
            var source = @"using System
function Main(): i32
{
    var a = [1, 2]
    var b = [..a, 4]
    return 0
}";
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
            var diagnostics = compilation.GetDiagnostics();
            Assert.True(diagnostics.HasErrors());
            Assert.True(diagnostics.Any(d => d.Message.Contains("spread")));
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
