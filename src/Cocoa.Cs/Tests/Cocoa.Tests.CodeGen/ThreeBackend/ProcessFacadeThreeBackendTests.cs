using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.Targeting;
using Cocoa.CodeGen.Native;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;
using Cocoa.Tests.CodeGen.Emit.Native;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeGen.ThreeBackend
{
    /// <summary>
    /// System.Diagnostics.Process / ProcessStartInfo facade 三后端锁定（2026-10-18）：
    /// Start（不阻塞）→ WaitForExit（阻塞取退出码）→ ExitCode。IL 直连 BCL；native/Evaluator 走 LaunchProcess syscall。
    /// </summary>
    public class ProcessFacadeThreeBackendTests
    {
        private static string[] References() => new[]
        {
            typeof(object).Assembly.Location,
            typeof(System.Console).Assembly.Location,
            typeof(System.Diagnostics.Process).Assembly.Location,
        };

        private const string Program = @"using System
using System.Diagnostics

function Main(): i32
{
    let psi = new ProcessStartInfo(""cmd.exe"")
    psi.Arguments = ""/c exit 0""
    let p = Process.Start(psi)
    p.WaitForExit()
    System.Console.WriteLine(p.ExitCode == 0)
    let psi2 = new ProcessStartInfo()
    psi2.FileName = ""cmd.exe""
    psi2.Arguments = ""/c exit 3""
    let p2 = Process.Start(psi2)
    p2.WaitForExit()
    System.Console.WriteLine(p2.ExitCode == 3)
    return 0
}";

        private const string Expected = "True\nTrue\n";

        [Fact]
        public void Evaluator_Process()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
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
        public void IlE2e_Process()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-proc", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "proc.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var diagnostics = compilation.Emit("proce2e", References(), exePath, IlTarget.Parse("net9.0"));
            Assert.Empty(string.Join("\n", diagnostics));
            Assert.True(File.Exists(exePath));

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
        public void NativeE2e_Process(string target)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-proc", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "proc-" + target + ".exe");
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var diagnostics = compilation.EmitNative("procsdk", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(Expected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
        }
    }
}
