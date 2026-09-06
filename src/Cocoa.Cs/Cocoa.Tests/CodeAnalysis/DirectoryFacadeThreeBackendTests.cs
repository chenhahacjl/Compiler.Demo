using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.Targeting;
using Cocoa.CodeGen.Native;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Tests.CodeAnalysis.Emit.Native;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// System.IO.Directory facade 三后端锁定（2026-09-06）：Exists / CreateDirectory / GetCurrentDirectory。
    /// IL 直连 BCL；native 走 ucrt _wmkdir + GetFileAttributesW；Directory.CreateDirectory 幂等。
    /// </summary>
    public class DirectoryFacadeThreeBackendTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Program = @"using System
using System.IO

function Main(): i32
{
    let cwd = Directory.GetCurrentDirectory()
    System.Console.WriteLine(cwd.Length > 0)
    System.Console.WriteLine(Directory.Exists(cwd))
    let d = cwd + ""\\__cocoa_dir_test""
    Directory.CreateDirectory(d)
    System.Console.WriteLine(Directory.Exists(d))
    return 0
}";

        private const string Expected = "True\nTrue\nTrue\n";

        [Fact]
        public void Evaluator_Directory()
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
                Assert.True(actual == Expected, "DIAG:\n" + string.Join("\n", result.Diagnostics.Select(d => d.Message)) + "\nGOT:\n" + actual);
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        [Fact]
        public void IlE2e_Directory()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-dirfac", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "dir.exe");
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var diagnostics = compilation.Emit("dirtest", References(), exePath, IlTarget.Parse("net9.0"));
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

        // x86 native 在 8 字节返回修复后仍异常（runner 卡子进程），x86 native 并入原语冒烟专项（阶段5）
        [Theory]
        [InlineData("windows-x64")]
        public void NativeE2e_Directory(string target)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-dirfac", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var exePath = Path.Combine(dir, "dir-" + target + ".exe");
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var diagnostics = compilation.EmitNative("dirsdk", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            var stdout = NativeEmitTests.Run(exePath);
            Assert.Equal(Expected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
        }
    }
}