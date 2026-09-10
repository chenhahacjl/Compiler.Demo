using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeGen.Native;
using Cocoa.Targeting;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 6e-M33：.coa 基类序列化——同库可序列化基类的继承链（cls `base:` 字段 + 实例方法/构造 fn +
    /// `ctorchain` 构造链节点）跨库 round-trip 与三后端消费（Handle 族 FileHandle extends Handle 硬前置）。
    /// </summary>
    public class CodInheritanceThreeBackendTests
    {
        private const string LibSource = @"namespace MyLib
{
    class Base
    {
        public field Raw: nint

        public function GetRaw(): nint
        {
            return Raw
        }
    }

    class Derived extends Base
    {
        public field Extra: i32

        public function Sum(): i64
        {
            return i64(Raw) + i64(Extra)
        }
    }
}";

        private const string Consumer = @"using System
using MyLib

function Main(): i32
{
    let x = new Derived()
    x.Raw = 40
    x.Extra = 2
    Console.WriteLine(x.Sum())
    Console.WriteLine(x.GetRaw() == 40)
    return 0
}";

        private const string Expected = "42\nTrue\n";

        // ---- .coa 写侧：base: 字段 + ctorchain 节点 ----------------------------------

        [Fact]
        public void EmitCocoa_Serializes_BaseType_And_CtorChain()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-codinherit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var output = Path.Combine(dir, "Lib.coa");

            var compilation = Compilation.Create(SyntaxTree.Parse(LibSource));
            var diagnostics = compilation.EmitCocoa("Lib", output);

            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));
            var text = File.ReadAllText(output);
            Assert.Contains("base:MyLib.Base", text);
            Assert.Contains("owner:MyLib.Derived", text);
            Assert.Contains("ctorchain base Lib!MyLib.Base.Base[]", text);
            Assert.Contains("owner:MyLib.Base", text);
        }

        [Fact]
        public void Read_Coa_Restores_BaseType_Chain()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-codinherit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var output = Path.Combine(dir, "Lib.coa");

            var compilation = Compilation.Create(SyntaxTree.Parse(LibSource));
            Assert.True(compilation.EmitCocoa("Lib", output).IsEmpty);

            var cod = CoaSerializer.Read(File.ReadAllText(output), "Lib", ImmutableArray<CoaProgram>.Empty);
            var baseType = cod.Classes.First(c => c.Name == "Base");
            var derived = cod.Classes.First(c => c.Name == "Derived");

            Assert.Equal(baseType, derived.BaseType);
            Assert.Same(baseType, derived.BaseType);
        }

        // ---- 三后端消费 ------------------------------------------------------------

        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        [Fact]
        public void Evaluator_Consumer_Inheritance()
        {
            var lib = CompileLib();
            var original = Console.Out;
            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);

                var compilation = Compilation.Create("Main", new[] { lib, typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, SyntaxTree.Parse(Consumer));
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
        public void Il_E2e_Consumer_Inheritance()
        {
            var lib = CompileLib();
            var (exitCode, stdout) = EmitIlAndRun(Consumer, lib, "cod-inherit-il");
            Assert.Equal(0, exitCode);
            Assert.Equal(Expected, stdout.Replace("\r\n", "\n"));
        }

        public static IEnumerable<object[]> GetPlatforms()
        {
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X64) };
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X86) };
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Native_E2e_Consumer_Inheritance(object platform)
        {
            var lib = CompileLib();
            var (exitCode, stdout) = EmitNativeAndRun(Consumer, lib, "cod-inherit-native", (TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal(Expected, stdout);
        }

        private static string CompileLib()
        {
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-codinherit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var output = Path.Combine(dir, "Lib.coa");
            var compilation = Compilation.Create(SyntaxTree.Parse(LibSource));
            var diagnostics = compilation.EmitCocoa("Lib", output);
            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));
            return output;
        }

        private static (int ExitCode, string Stdout) EmitIlAndRun(string source, string libPath, string name)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create("Main", new[] { libPath, typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, syntaxTree);
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-codinherit-il");
            Directory.CreateDirectory(directory);
            var exePath = Path.Combine(directory, name + ".exe");
            var diagnostics = compilation.Emit(name, new[] { libPath, typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, exePath, IlTarget.Parse("net9.0"));

            Assert.Empty(diagnostics);
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo("dotnet", "\"" + exePath + "\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);
            return (process.ExitCode, stdout);
        }

        private static (int ExitCode, string Stdout) EmitNativeAndRun(string source, string libPath, string name, TargetPlatform platform)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create("Main", new[] { libPath }, syntaxTree);
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-codinherit-native");
            Directory.CreateDirectory(directory);
            var suffix = platform.Arch == Architecture.X86 ? "-x86" : "";
            var exePath = Path.Combine(directory, name + suffix + ".exe");
            var diagnostics = compilation.EmitNative(name, exePath, platform);

            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            using var output = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new TimeoutException("Native exe did not exit in time.");
            }

            outputTask.Wait();
            var stdout = Encoding.Unicode.GetString(output.ToArray()).Replace("\r\n", "\n").Replace("\r", "\n");
            return (process.ExitCode, stdout);
        }
    }
}