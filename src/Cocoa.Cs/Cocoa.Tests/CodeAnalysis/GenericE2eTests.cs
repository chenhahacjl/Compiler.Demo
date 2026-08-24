using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.IL;
using Cocoa.CodeAnalysis.Emit.Native;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// 泛型单态化端到端测试（6e-M20 G2）：Box&lt;T&gt; / 嵌套泛型 / 约束——Evaluator + IL + native x64/x86。
    /// </summary>
    public class GenericE2eTests
    {
        private const string BoxProgram = @"using System

public class Box<T>
{
    private _value: T

    public constructor(value: T)
    {
        _value = value
    }

    public function Get(): T
    {
        return _value
    }
}

function Main(): i32
{
    var bi = new Box<i32>(42)
    Console.WriteLine(bi.Get())

    var bs = new Box<string>(""hello"")
    Console.WriteLine(bs.Get())

    if bi.Get() != 42
    {
        return 1
    }

    return 0
}";

        private const string PairProgram = @"using System

public class Pair<K, V>
{
    private _key: K
    private _value: V

    public constructor(key: K, value: V)
    {
        _key = key
        _value = value
    }

    public function Key(): K
    {
        return _key
    }

    public function Value(): V
    {
        return _value
    }
}

public class Entry<E>
{
    private _inner: E

    public constructor(inner: E)
    {
        _inner = inner
    }

    public function Inner(): E
    {
        return _inner
    }
}

function Main(): i32
{
    var p = new Pair<i32, string>(7, ""seven"")
    Console.WriteLine(p.Key())
    Console.WriteLine(p.Value())

    var nested = new Entry<Pair<i32, string>>(p)
    var inner = nested.Inner()
    Console.WriteLine(inner.Value())

    if inner.Key() != 7
    {
        return 1
    }

    return 0
}";

        private const string SwapProgram = @"using System

function Swap<T>(a: T, b: T): T
{
    return a
}

function Main(): i32
{
    var result = Swap<i32>(7, 9)
    Console.WriteLine(result)
    var s = Swap<string>(""first"", ""second"")
    Console.WriteLine(s)

    if result != 7
    {
        return 1
    }

    return 0
}";

        // ------------------------------------------------------------------
        // Evaluator
        // ------------------------------------------------------------------

        [Fact]
        public void Evaluator_BoxProgram_ReturnsZero()
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(BoxProgram));
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Evaluator_NestedGeneric_ReturnsZero()
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(PairProgram));
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Evaluator_GenericMethod_ExplicitArguments()
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(SwapProgram));
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Evaluator_ConstraintViolation_Diagnosed()
        {
            var code = @"using System

public class Store<T> where T: class
{
    private _value: T

    public constructor(value: T)
    {
        _value = value
    }
}

function Main()
{
    var s = new Store<i32>(5)
    Console.WriteLine(""unreachable"")
}";
            var compilation = Compilation.Create(SyntaxTree.Parse(code));
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Contains(result.Diagnostics, d => d.IsError && d.Message.Contains("where T: class"));
        }

        // ------------------------------------------------------------------
        // IL（dotnet 宿主）
        // ------------------------------------------------------------------

        [Fact]
        public void Il_BoxProgram_WritesValues()
        {
            var (exitCode, stdout) = EmitIlAndRun(BoxProgram, "generic_box_il");
            Assert.Equal(0, exitCode);
            Assert.Equal("42\nhello\n", stdout);
        }

        [Fact]
        public void Il_NestedGeneric_WritesValues()
        {
            var (exitCode, stdout) = EmitIlAndRun(PairProgram, "generic_pair_il");
            Assert.Equal(0, exitCode);
            Assert.Equal("7\nseven\nseven\n", stdout);
        }

        [Fact]
        public void Il_GenericMethod_WritesValues()
        {
            var (exitCode, stdout) = EmitIlAndRun(SwapProgram, "generic_swap_il");
            Assert.Equal(0, exitCode);
            Assert.Equal("7\nfirst\n", stdout);
        }

        private static (int ExitCode, string Stdout) EmitIlAndRun(string source, string name)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var references = new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };
            var compilation = Compilation.Create("Main", references, syntaxTree);
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-generic-il-tests");
            Directory.CreateDirectory(directory);
            var exePath = Path.Combine(directory, name + ".exe");
            var diagnostics = compilation.Emit(name, references, exePath, IlTarget.Parse("net9.0"));

            Assert.Empty(diagnostics);

            var psi = new ProcessStartInfo("dotnet", $"\"{exePath}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);

            return (process.ExitCode, stdout.Replace("\r\n", "\n"));
        }

        // ------------------------------------------------------------------
        // native x64 / x86
        // ------------------------------------------------------------------

        public static IEnumerable<object[]> GetPlatforms()
        {
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X64) };
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X86) };
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Native_BoxProgram_WritesValues(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(BoxProgram, "generic_box_native", (TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal("42\nhello\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Native_NestedGeneric_WritesValues(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(PairProgram, "generic_pair_native", (TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal("7\nseven\nseven\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Native_GenericMethod_WritesValues(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(SwapProgram, "generic_swap_native", (TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal("7\nfirst\n", stdout);
        }

        private static (int ExitCode, string Stdout) EmitNativeAndRun(string source, string name, TargetPlatform platform)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-generic-native-tests");
            Directory.CreateDirectory(directory);
            var suffix = platform.Arch == Architecture.X86 ? "-x86" : "";
            var exePath = Path.Combine(directory, name + suffix + ".exe");
            var diagnostics = compilation.EmitNative(name, exePath, platform);

            Assert.True(diagnostics.IsEmpty, string.Join("; ", diagnostics.Select(d => d.Message)));
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
