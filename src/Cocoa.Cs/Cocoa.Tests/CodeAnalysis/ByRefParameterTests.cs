using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.Native;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Tests.CodeAnalysis.Emit.IL;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// out/ref 参数端到端与语义矩阵（6e-M23 R9）：
    /// 三后端（native x64/x86 + IL）× lvalue 五类、明确赋值分析三诊断、
    /// lambda 捕获拒绝、函数类型 byref 拦截、重载共存、TryParse stdlib 消费。
    /// </summary>
    public class ByRefParameterTests
    {
        // ------------------------------------------------------------------
        // 三后端 e2e：单一程序覆盖 lvalue 五类 + TryParse，断言各后端输出一致
        // ------------------------------------------------------------------

        private const string MatrixProgram = @"
function Inc(ref x: i32): void
{
    x = x + 1
}

function Get99(out v: i32): bool
{
    v = 99
    return true
}

class Counter
{
    public static s: i32
    private _n: i32

    public function Take(out old: i32): void
    {
        old = _n
        _n = _n + 10
    }
}

function Main(): void
{
    var a: i32 = 5
    Inc(ref a)
    System.Console.WriteLine(a)

    var b: i32 = 0
    if Get99(out b)
    {
        System.Console.WriteLine(b)
    }

    let arr = new i32[2] {1, 2}
    Inc(ref arr[1])
    System.Console.WriteLine(arr[1])

    Counter.s = 7
    Inc(ref Counter.s)
    System.Console.WriteLine(Counter.s)

    let c = new Counter()
    var old: i32 = -1
    c.Take(out old)
    System.Console.WriteLine(old)

    var p: i32 = 0
    if Int32.TryParse(""12345"", out p)
    {
        System.Console.WriteLine(p)
    }

    if Int32.TryParse(""12ab"", out p)
    {
        System.Console.WriteLine(p)
    }
    else
    {
        System.Console.WriteLine(""rej"")
    }
}";

        public static IEnumerable<object[]> GetBackends()
        {
            yield return new object[] { "native-x64" };
            yield return new object[] { "native-x86" };
            yield return new object[] { "il" };
        }

        [Theory]
        [MemberData(nameof(GetBackends))]
        public void ByRef_Matrix_ThreeBackends(string backend)
        {
            const string expected = "6\n99\n3\n8\n0\n12345\nrej\n";

            if (backend == "il")
            {
                var (exitCode, stdout) = IlE2eTests.EmitAndRun(MatrixProgram, "ByRefMatrix");
                Assert.Equal(0, exitCode);
                Assert.Equal(expected, stdout.Replace("\r\n", "\n"));
                return;
            }

            var platform = backend == "native-x86"
                ? new TargetPlatform(TargetOS.Windows, Architecture.X86)
                : new TargetPlatform(TargetOS.Windows, Architecture.X64);
            var (code, output) = EmitNativeAndRun(MatrixProgram, "ByRefMatrix", platform);
            Assert.Equal(0, code);
            Assert.Equal(expected, output);
        }

        private static (int ExitCode, string Stdout) EmitNativeAndRun(string source, string name, TargetPlatform platform)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cocoa-byref-tests");
            Directory.CreateDirectory(directory);
            var exePath = System.IO.Path.Combine(directory, name + (platform.Arch == Architecture.X86 ? "-x86" : "-x64") + ".exe");
            var diagnostics = compilation.EmitNative(name, exePath, platform);

            Assert.True(diagnostics.IsEmpty, string.Join("\n", diagnostics.Select(d => d.Message)));
            Assert.True(File.Exists(exePath));

            var psi = new System.Diagnostics.ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            using var output = new System.IO.MemoryStream();
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

        // ------------------------------------------------------------------
        // 明确赋值分析（Evaluator 路径诊断）
        // ------------------------------------------------------------------

        private static ImmutableDiagnosticList Evaluate(string source)
        {
            var tree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(tree);
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            return new ImmutableDiagnosticList(result.Diagnostics.Where(d => d.IsError).Select(d => d.Message).ToList());
        }

        private sealed class ImmutableDiagnosticList
        {
            private readonly List<string> _messages;
            public ImmutableDiagnosticList(List<string> messages) { _messages = messages; }
            public bool Has(string fragment) => _messages.Any(m => m.Contains(fragment));
            public IReadOnlyList<string> All => _messages;
        }

        [Fact]
        public void Dfa_OutParameterNotAssignedOnReturn_Diagnosed()
        {
            var messages = Evaluate(@"
function Get(flag: bool, out v: i32): i32
{
    if flag
    {
        v = 1
    }
    return v
}
var z: i32 = 0
Get(true, out z)
");
            Assert.True(messages.Has("必须在返回前赋值"), string.Join("|", messages.All));
        }

        [Fact]
        public void Dfa_UseOfUnassignedOutParameter_Diagnosed()
        {
            var messages = Evaluate(@"
function Get(out v: i32): void
{
    System.Console.WriteLine(v)
    v = 1
}
var z: i32 = 0
Get(out z)
");
            Assert.True(messages.Has("使用了未赋值的 out 参数"), string.Join("|", messages.All));
        }

        [Fact]
        public void Dfa_RefArgumentRequiresAssigned_Diagnosed()
        {
            var messages = Evaluate(@"
function Sink(ref x: i32): void
{
}

function Source(out v: i32): void
{
    Sink(ref v)
    v = 1
}
var z: i32 = 0
Source(out z)
");
            Assert.True(messages.Has("不能作为 'ref' 实参传递"), string.Join("|", messages.All));
        }

        [Fact]
        public void Dfa_AssignedInBothBranches_Passes()
        {
            var messages = Evaluate(@"
function Get(flag: bool, out v: i32): void
{
    if flag
    {
        v = 1
    }
    else
    {
        v = 2
    }
}
var z: i32 = 0
Get(true, out z)
System.Console.WriteLine(z)
");
            Assert.Empty(messages.All);
        }

        // ------------------------------------------------------------------
        // 边界拦截
        // ------------------------------------------------------------------

        [Fact]
        public void Lambda_CaptureOfOutParameter_Rejected()
        {
            var messages = Evaluate(@"
function Run(out v: i32): void
{
    let f = () => v
    v = 1
}
var z: i32 = 0
Run(out z)
");
            Assert.True(messages.Has("lambda 不能捕获 out/ref 形参"), string.Join("|", messages.All));
        }

        [Fact]
        public void MethodGroup_WithByRefSignature_NotConvertibleToFunctionType()
        {
            var messages = Evaluate(@"
function TryParseLike(s: string, out value: i32): bool
{
    value = 0
    return true
}
function Main(): void
{
    let f = TryParseLike
}
");
            Assert.True(messages.Has("函数类型") || messages.Has("byref"), string.Join("|", messages.All));
        }

        [Fact]
        public void Overloads_DifferingOnlyByModifier_Coexist()
        {
            var messages = Evaluate(@"
function F(x: i32): i32
{
    return x * 2
}
function F(out x: i32): i32
{
    x = 7
    return x
}
var a: i32 = 3
let r1 = F(a)
var b: i32 = 0
let r2 = F(out b)
System.Console.WriteLine(r1)
System.Console.WriteLine(r2)
");
            Assert.Empty(messages.All);
        }

        [Fact]
        public void CallSite_MissingOutModifier_Diagnosed()
        {
            var messages = Evaluate(@"
function Get(out v: i32): void
{
    v = 1
}
var z: i32 = 0
Get(z)
");
            Assert.True(messages.Has("实参须写 'out 变量'"), string.Join("|", messages.All));
        }

        [Fact]
        public void Property_AsOutArgument_Rejected_AlignedWithCSharp()
        {
            var messages = Evaluate(@"
class Box
{
    public property X: i32 { get set } = 0
}
function Sink(out x: i32): void
{
    x = 1
}
function Main(): void
{
    let b = new Box()
    Sink(out b.X)
}
");
            Assert.True(messages.Has("可赋值变量"), string.Join("|", messages.All));
        }
    }
}
