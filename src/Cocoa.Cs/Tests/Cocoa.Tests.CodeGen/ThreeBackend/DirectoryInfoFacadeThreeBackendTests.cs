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
    /// System.IO.DirectoryInfo facade + Directory.Delete 三后端锁定（2026-10-18）：
    /// Name/Parent/Exists/Create/Delete（RemoveDirectoryW 非递归）。IL 直连 BCL；native/Evaluator 走 IOSyscall。
    /// </summary>
    public class DirectoryInfoFacadeThreeBackendTests
    {
        private static string[] References() => new[]
        {
            typeof(object).Assembly.Location,
            typeof(System.Console).Assembly.Location,
            typeof(System.IO.FileInfo).Assembly.Location,
        };

        private static string Program(string dir, string child, string fresh)
        {
            var d = dir.Replace("\\", "/");
            var childPath = Path.Combine(dir, child).Replace("\\", "/");
            var freshPath = Path.Combine(dir, fresh).Replace("\\", "/");
            return @"using System
using System.IO

function Main(): i32
{
    let d = """ + d + @"""
    Directory.CreateDirectory(""" + childPath + @""")
    let di = new DirectoryInfo(""" + childPath + @""")
    System.Console.WriteLine(di.Exists)
    System.Console.WriteLine(di.Name == """ + child + @""")
    System.Console.WriteLine(di.Parent.Name == """ + Path.GetFileName(dir) + @""")
    let fresh = new DirectoryInfo(""" + freshPath + @""")
    fresh.Create()
    System.Console.WriteLine(Directory.Exists(""" + freshPath + @"""))
    fresh.Delete()
    System.Console.WriteLine(!Directory.Exists(""" + freshPath + @"""))
    System.Console.WriteLine(Directory.Exists(""" + childPath + @"""))
    return 0
}";
        }

        private const string Expected = "True\nTrue\nTrue\nTrue\nTrue\nTrue\n";

        [Fact]
        public void Evaluator_DirectoryInfo()
        {
            var dir = NewDir();
            try
            {
                var source = Program(dir, "sub", "fresh");
                var original = Console.Out;
                try
                {
                    using var writer = new StringWriter();
                    Console.SetOut(writer);
                    var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
                    var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                    Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                    Assert.Equal(Expected, writer.ToString().Replace("\r\n", "\n"));
                }
                finally
                {
                    Console.SetOut(original);
                }
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void IlE2e_DirectoryInfo()
        {
            var dir = NewDir();
            try
            {
                var source = Program(dir, "sub", "fresh");
                var exePath = Path.Combine(dir, "di-il.exe");
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
                var diagnostics = compilation.Emit("die2e", References(), exePath, IlTarget.Parse("net9.0"));
                Assert.Empty(string.Join("\n", diagnostics));
                Assert.True(File.Exists(exePath));

                var psi = new ProcessStartInfo("dotnet", $"\"{exePath}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using var process = Process.Start(psi)!;
                using var output = new MemoryStream();
                using var errOutput = new MemoryStream();
                var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
                var errTask = process.StandardError.BaseStream.CopyToAsync(errOutput);
                Assert.True(process.WaitForExit(20000));
                outputTask.Wait();
                errTask.Wait();
                var stderr = Encoding.UTF8.GetString(errOutput.ToArray());
                var stdout = Encoding.UTF8.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
                Assert.True(process.ExitCode == 0, "stderr: " + stderr);
                Assert.Equal(Expected, stdout);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_DirectoryInfo(string target)
        {
            var dir = NewDir();
            try
            {
                var source = Program(dir, "sub", "fresh");
                var exePath = Path.Combine(dir, "di-" + target + ".exe");
                TargetPlatform.TryParse(target, out var platform);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
                var diagnostics = compilation.EmitNative("disdk", exePath, platform);
                Assert.Empty(string.Join("\n", diagnostics));
                var stdout = NativeEmitTests.Run(exePath);
                Assert.Equal(Expected, stdout.Replace("\r\n", "\n").Replace("\r", "\n"));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        private static string NewDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-di", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
