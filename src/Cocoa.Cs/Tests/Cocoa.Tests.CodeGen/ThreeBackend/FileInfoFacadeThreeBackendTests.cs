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
    /// System.IO.FileInfo facade 三后端锁定（2026-10-18）：Name/Extension/DirectoryName/Exists/Length/Delete/CopyTo。
    /// IL 直连 BCL System.IO.FileInfo；native/Evaluator 走本体（FileSyscall/IOSyscall + Path）。
    /// </summary>
    public class FileInfoFacadeThreeBackendTests
    {
        private static string[] References() => new[]
        {
            typeof(object).Assembly.Location,
            typeof(System.Console).Assembly.Location,
            typeof(System.IO.FileInfo).Assembly.Location,
        };

        private static string Program(string dir, string fileName, string copyName)
        {
            var p = Path.Combine(dir, fileName).Replace("\\", "/");
            var copy = Path.Combine(dir, copyName).Replace("\\", "/");
            return @"using System
using System.IO

function Main(): i32
{
    let f = """ + p + @"""
    File.WriteAllText(f, ""hello"")
    let fi = new FileInfo(f)
    System.Console.WriteLine(fi.Exists)
    System.Console.WriteLine(fi.Name == """ + fileName + @""")
    System.Console.WriteLine(fi.Extension == """ + Path.GetExtension(fileName) + @""")
    System.Console.WriteLine(fi.DirectoryName.Length > 0)
    System.Console.WriteLine(File.Exists(fi.DirectoryName + ""/"" + fi.Name))
    System.Console.WriteLine(fi.Length == 5)
    let fc = fi.CopyTo(""" + copy + @""")
    System.Console.WriteLine(File.Exists(""" + copy + @"""))
    System.Console.WriteLine(fc.Length == 5)
    fi.Delete()
    System.Console.WriteLine(!File.Exists(f))
    System.Console.WriteLine(File.Exists(""" + copy + @"""))
    return 0
}";
        }

        private const string Expected = "True\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\nTrue\n";

        [Fact]
        public void Evaluator_FileInfo()
        {
            var dir = NewDir();
            try
            {
                var source = Program(dir, "a.txt", "b.txt");
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
        public void IlE2e_FileInfo()
        {
            var dir = NewDir();
            try
            {
                var source = Program(dir, "a.txt", "b.txt");
                var exePath = Path.Combine(dir, "fi-il.exe");
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
                var diagnostics = compilation.Emit("fie2e", References(), exePath, IlTarget.Parse("net9.0"));
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
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_FileInfo(string target)
        {
            var dir = NewDir();
            try
            {
                var source = Program(dir, "a.txt", "b.txt");
                var exePath = Path.Combine(dir, "fi-" + target + ".exe");
                TargetPlatform.TryParse(target, out var platform);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(source));
                var diagnostics = compilation.EmitNative("fisdk", exePath, platform);
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
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-fi", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
