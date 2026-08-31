using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.Native;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// Sha256Hash syscall: NIST FIPS 180-4 test vectors x Evaluator / IL.
    /// </summary>
    public class Sha256HashTests
    {
        private static string[] References() => new[]
        {
            typeof(object).Assembly.Location,
            typeof(System.Console).Assembly.Location,
            typeof(System.Security.Cryptography.SHA256).Assembly.Location,
        };

        private const string VectorProgram = @"using System

function Main(): i32
{
    var data: u8[] = new u8[3]
    data[0] = 97
    data[1] = 98
    data[2] = 99
    var hash = Runtime.Sha256Hash(data)
    var hex = Convert.ToHexString(hash)
    System.Console.WriteLine(hex)
    return 0
}";

        private const string EmptyProgram = @"using System

function Main(): i32
{
    var data: u8[] = new u8[0]
    var hash = Runtime.Sha256Hash(data)
    var hex = Convert.ToHexString(hash)
    System.Console.WriteLine(hex)
    return 0
}";

        [Fact]
        public void Evaluator_Sha256Hash_Vector()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(VectorProgram));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(result.Diagnostics.Where(d => d.IsError).Any() == false,
                    string.Join("\n", result.Diagnostics.Where(d => d.IsError).Select(d => d.Message)));
                var output = writer.ToString().Trim();
                Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", output);
            }
            finally { Console.SetOut(original); }
        }

        [Fact]
        public void Evaluator_Sha256Hash_Empty()
        {
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(EmptyProgram));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(result.Diagnostics.Where(d => d.IsError).Any() == false,
                    string.Join("\n", result.Diagnostics.Where(d => d.IsError).Select(d => d.Message)));
                var output = writer.ToString().Trim();
                Assert.Equal("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", output);
            }
            finally { Console.SetOut(original); }
        }

        [Fact]
        public void Il_Sha256Hash_Compiles()
        {
            var syntaxTree = SyntaxTree.Parse(VectorProgram);
            var compilation = Compilation.Create("Main", References(), syntaxTree);
            var exePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cocoa-sha256", "sha-il.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
            var diagnostics = compilation.Emit("sha-il", References(), exePath,
                Cocoa.CodeAnalysis.Emit.IL.IlTarget.Parse("net9.0"));
            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));
        }

        private static (int ExitCode, string Stdout) EmitNativeAndRun(string source, string name)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create("Main", References(), syntaxTree);
            var platform = new TargetPlatform(TargetOS.Windows, Architecture.X64);
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-sha256-native");
            Directory.CreateDirectory(directory);
            var exePath = Path.Combine(directory, name + ".exe");
            var diagnostics = compilation.EmitNative(name, exePath, platform);
            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            using var errOutput = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            var errTask = process.StandardError.BaseStream.CopyToAsync(errOutput);
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new TimeoutException("Native exe did not exit in time.");
            }
            outputTask.Wait();
            errTask.Wait();
            var stdout = Encoding.Unicode.GetString(output.ToArray());
            var stderr = Encoding.UTF8.GetString(errOutput.ToArray());
            if (!string.IsNullOrEmpty(stderr))
                stdout += "\n[STDERR] " + stderr;
            return (process.ExitCode, stdout);
        }

        [Fact]
        public void Native_Sha256Hash_Vector()
        {
            var (exitCode, stdout) = EmitNativeAndRun(VectorProgram, "sha256-vec");
            Assert.Equal(0, exitCode);
            Assert.Contains("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", stdout);
        }

        [Fact]
        public void Native_Sha256Hash_Empty()
        {
            var (exitCode, stdout) = EmitNativeAndRun(EmptyProgram, "sha256-empty");
            Assert.Equal(0, exitCode);
            Assert.Contains("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", stdout);
        }

        private const string MinimalProgram = @"using System

function Main(): i32
{
    System.Console.WriteLine(""hello"")
    return 0
}";

        [Fact]
        public void Native_Minimal_Program()
        {
            var (exitCode, stdout) = EmitNativeAndRun(MinimalProgram, "native-min");
            Assert.Equal(0, exitCode);
            Assert.Contains("hello", stdout);
        }

        private const string Sha256OnlyProgram = @"using System

function Main(): i32
{
    var data: u8[] = new u8[3]
    data[0] = 97
    data[1] = 98
    data[2] = 99
    var hash = Runtime.Sha256Hash(data)
    if (hash == null)
    {
        System.Console.WriteLine(""null"")
    }
    else
    {
        System.Console.WriteLine(""ok"")
    }
    return 0
}";

        [Fact]
        public void Native_Sha256Hash_Only()
        {
            var (exitCode, stdout) = EmitNativeAndRun(Sha256OnlyProgram, "sha256-only");
            Assert.Equal(0, exitCode);
            Assert.Contains("ok", stdout);
        }
    }
}
