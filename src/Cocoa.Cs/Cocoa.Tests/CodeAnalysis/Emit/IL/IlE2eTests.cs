using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Cocoa.CodeAnalysis.Emit.IL;
using Xunit;
namespace Cocoa.Tests.CodeAnalysis.Emit.IL
{
    public class IlE2eTests
    {
        private static string GetOutputPath(string name)
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-il-tests");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, name + ".exe");
        }

        internal static (int ExitCode, string Stdout) EmitAndRun(string source, string name, string? input = null)
            => EmitAndRun(source, name, "Main", input, null, useCs: false);

        private static (int ExitCode, string Stdout) EmitAndRunCs(string source, string name, string? input = null)
            => EmitAndRun(source, name, "Main", input, null, useCs: true);

        private static (int ExitCode, string Stdout) EmitAndRun(string source, string name, string entryPointName, string? input = null, string[]? processArgs = null, bool useCs = false)
        {
            var syntaxTree = useCs ? Cocoa.CodeAnalysis.Syntax.SyntaxTree.ParseCs(source) : Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(source);
            var compilation = Cocoa.CodeAnalysis.Compilation.Create(entryPointName, new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, syntaxTree);
            var exePath = GetOutputPath(name);
            // netcore：托管 exe + runtimeconfig，由 `dotnet <exe>` 运行（netfx 不写 runtimeconfig）
            var diagnostics = compilation.Emit(name, new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, exePath, IlTarget.Parse("net9.0"));

            Assert.Empty(diagnostics);
            Assert.True(File.Exists(exePath));

            var arguments = $"\"{exePath}\"";
            if (processArgs != null)
            {
                arguments += " " + string.Join(" ", processArgs);
            }

            var psi = new ProcessStartInfo("dotnet", arguments)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi)!;
            if (input != null)
            {
                process.StandardInput.Write(input);
            }

            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);
            return (process.ExitCode, stdout);
        }

        [Fact]
        public void Run_CocoaProgram_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    var sum = 0
    var i = 0
    while i < 5
    {
        sum = sum + i
        i = i + 1
    }
    Console.WriteLine(sum)
    var name = Console.ReadLine()
    Console.WriteLine(""hello "" + name)
    Console.WriteLine(sum > 10)
    var r = Runtime.Random(100)
    if r >= 0 && r < 100
    {
        Console.WriteLine(""ok"")
    }
}", "e2e-builtins", "World");

            Assert.Equal(0, exitCode);
            Assert.Equal("10\r\nhello World\r\nFalse\r\nok\r\n", stdout);
        }

        [Fact]
        public void Run_SyscallFunction_MemberCall_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

class Runtime
{
    syscall function Random(max: i32): i32
}

function Main()
{
    var r = Runtime.Random(100)
    if r >= 0 && r < 100
    {
        Console.WriteLine(""ok"")
    }
}", "e2e-syscall-member");

            Assert.Equal(0, exitCode);
            Assert.Equal("ok\r\n", stdout);
        }

        [Fact]
        public void Run_SyscallFunction_MemberCall_Print_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

class Runtime
{
    syscall function WriteLine(text: string): void
}

function Main()
{
    Runtime.WriteLine(""hello syscall"")
}", "e2e-syscall-print");

            Assert.Equal(0, exitCode);
            Assert.Equal("hello syscall\r\n", stdout);
        }

        [Fact]
        public void Run_Builtin_SleepTickCountExit_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    var t0 = Runtime.TickCount()
    Runtime.Sleep(1)
    var t1 = Runtime.TickCount()
    if t1 >= t0
    {
        Console.WriteLine(""ok"")
    }
}", "e2e-sleep-now");

            Assert.Equal(0, exitCode);
            Assert.Equal("ok\r\n", stdout);
        }

        [Fact]
        public void Run_LogicalOperators_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    var t = true
    var f = false
    Console.WriteLine(t && f)
    Console.WriteLine(t && true)
    Console.WriteLine(t || f)
    Console.WriteLine(f || f)
}", "e2e-logical-operators");

            Assert.Equal(0, exitCode);
            Assert.Equal("False\r\nTrue\r\nTrue\r\nFalse\r\n", stdout);
        }

        [Fact]
        public void Run_Builtin_Beep_OnDotnetHost()        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    Runtime.Beep(800, 50)
    Console.WriteLine(""beeped"")
}", "e2e-beep");

            Assert.Equal(0, exitCode);
            Assert.Equal("beeped\r\n", stdout);
        }

        [Fact]
        public void Run_Builtin_Exit_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    Runtime.Exit(7)
    Console.WriteLine(""unreachable"")
}", "e2e-exit");

            Assert.Equal(7, exitCode);
            Assert.Equal("", stdout);
        }

        [Fact]
        public void Run_Builtin_MathPrimitives_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    Console.WriteLine(Runtime.Sqrt(2.0))
    Console.WriteLine(Runtime.Floor(2.7))
    Console.WriteLine(Runtime.Floor(-2.7))
    Console.WriteLine(Runtime.Ceiling(2.1))
    Console.WriteLine(Runtime.Ceiling(-2.1))
    Console.WriteLine(Runtime.Truncate(2.7))
    Console.WriteLine(Runtime.Truncate(-2.7))
    Console.WriteLine(Runtime.Round(2.5))
    Console.WriteLine(Runtime.Round(3.5))
    Console.WriteLine(Runtime.Round(-2.5))
    Console.WriteLine(Runtime.Sqrt(0.0))
}", "e2e-math-primitives");

            // round 为 banker's rounding（最近偶数）：2.5→2、3.5→4、-2.5→-2
            Assert.Equal(
                "1.4142135623730951\r\n" +
                "2\r\n" +
                "-3\r\n" +
                "3\r\n" +
                "-2\r\n" +
                "2\r\n" +
                "-2\r\n" +
                "2\r\n" +
                "4\r\n" +
                "-2\r\n" +
                "0\r\n", stdout);
            Assert.Equal(0, exitCode);
        }

        [Fact]
        public void Run_DefaultInitializedVariables_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    var a: i32
    var b: bool
    var d: f64
    var c: char
    var by: u8
    var s: string
    Console.WriteLine(a)
    Console.WriteLine(b)
    Console.WriteLine(d)
    Console.WriteLine(i32(c))
    Console.WriteLine(i32(by))
    Console.WriteLine(s == s)
    const x: i32 = 42
    Console.WriteLine(x)
    const y = 7
    Console.WriteLine(y + 1)
    var t = x
    t = t + 1
    Console.WriteLine(t)
}", "e2e-default-init");

            Assert.Equal(0, exitCode);
            Assert.Equal("0\r\nFalse\r\n0\r\n0\r\n0\r\nTrue\r\n42\r\n8\r\n43\r\n", stdout);
        }

        [Fact]
        public void Run_CocoaProgram_WithUserFunctions_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function add(a: i32, b: i32): i32
{
    return a + b
}

function square(x: i32): i32
{
    return x * x
}

function greet(name: string): string
{
    return ""Hello, "" + name
}

function fib(n: i32): i32
{
    if n <= 1
    {
        return n
    }
    return fib(n - 1) + fib(n - 2)
}

function isPositive(n: i32): bool
{
    return n > 0
}

function Main()
{
    Console.WriteLine(add(2, 3))
    Console.WriteLine(square(add(1, 2)))
    Console.WriteLine(greet(""Cocoa""))
    Console.WriteLine(fib(10))
    Console.WriteLine(isPositive(7))
    Console.WriteLine(isPositive(0 - 3))
    Console.WriteLine(add(fib(6), fib(7)))
}", "e2e-user-functions");

            Assert.Equal(0, exitCode);
            Assert.Equal("5\r\n9\r\nHello, Cocoa\r\n55\r\nTrue\r\nFalse\r\n21\r\n", stdout);
        }

        [Fact]
        public void Run_CocoaProgram_WithPInvoke_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

class Kernel32
{
    import kernel32.dll
    {
        static stdcall function GetTickCount(): i32
    }
}

function Main()
{
    var t = Kernel32.GetTickCount()
    if t > 0
    {
        Console.WriteLine(""up"")
    }
}", "e2e-pinvoke");

            Assert.Equal(0, exitCode);
            Assert.Equal("up\r\n", stdout);
        }

        [Fact]
        public void Run_CocoaProgram_WithPInvokeArguments_Stdcall_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
class Kernel32
{
    import kernel32.dll
    {
        static stdcall function ExitProcess(exitCode: i32)
    }
}

function Main()
{
    Kernel32.ExitProcess(42)
}", "e2e-pinvoke-stdcall-args");

            // 退出码 42 证明 int 参数正确穿越 P/Invoke 桩到达 native
            Assert.Equal(42, exitCode);
            Assert.Equal("", stdout);
        }

        [Fact]
        public void Run_CocoaProgram_WithPInvokeArguments_Cdecl_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
class Kernel32
{
    import kernel32.dll
    {
        static cdecl function ExitProcess(exitCode: i32)
    }
}

function Main()
{
    Kernel32.ExitProcess(7)
}", "e2e-pinvoke-cdecl-args");

            // x64 上 cdecl/stdcall 无差异，验证 cdecl 关键字全链路（ImplMap 0x0200 + 参数穿越）
            Assert.Equal(7, exitCode);
            Assert.Equal("", stdout);
        }

        [Fact]
        public void Run_CocoaProgram_WithPInvokeArguments_PointerParam_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

class Kernel32
{
    import kernel32.dll
    {
        static stdcall function GetModuleHandleW(moduleName: i32): i32
    }
}

function Main()
{
    var h = Kernel32.GetModuleHandleW(0)
    if h != 0
    {
        Console.WriteLine(""ok"")
    }
}", "e2e-pinvoke-pointer-param");

            // int 字面量 0 → LPWSTR(NULL)：模块基址句柄必非 0
            Assert.Equal(0, exitCode);
            Assert.Equal("ok\r\n", stdout);
        }

        [Fact]
        public void Run_CocoaProgram_WithControlFlow_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    var total = 0
    for var i = 1 to 5
    {
        if i == 3
        {
            continue
        }
        total = total + i
    }
    Console.WriteLine(total)

    var j = 0
    do
    {
        j = j + 1
    } while j < 3
    Console.WriteLine(j)

    var m = 0
    for var k = 1 to 10
    {
        if k > 2
        {
            break
        }
        m = m + k
    }
    Console.WriteLine(m)

    var nested = 0
    var p = 2
    while p > 0
    {
        var q = p
        while q > 0
        {
            nested = nested + q
            q = q - 1
        }
        p = p - 1
    }
    Console.WriteLine(nested)
}", "e2e-control-flow");

            Assert.Equal(0, exitCode);
            Assert.Equal("12\r\n3\r\n3\r\n4\r\n", stdout);
        }

        [Fact]
        public void Run_CocoaProgram_WithWideCallAndLongConcat_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function sum10(a: i32, b: i32, c: i32, d: i32, e: i32, f: i32, g: i32, h: i32, i: i32, j: i32): i32
{
    return a + b + c + d + e + f + g + h + i + j
}

function Main()
{
    let name = ""Cocoa""
    var x = ""1""
    var y = ""2""
    Console.WriteLine(""a"" + x + ""b"" + y + ""c"" + name)
    Console.WriteLine(sum10(1, 2, 3, 4, 5, 6, 7, 8, 9, 10))
    Console.WriteLine(name + ""!"")
}", "e2e-wide-call-long-concat");

            Assert.Equal(0, exitCode);
            Assert.Equal("a1b2cCocoa\r\n55\r\nCocoa!\r\n", stdout);
        }

        [Fact]
        public void Run_CocoaProgram_MainWithIntReturn_OnDotnetHost()
        {
            // main(): int 的返回值成为进程退出码（入口统一为 static int Main()）
            var (exitCode, stdout) = EmitAndRun(@"
function Main(): i32
{
    return 7
}", "e2e-main-int-return");

            Assert.Equal(7, exitCode);
            Assert.Equal("", stdout);
        }

        [Fact]
        public void Main_WithIntReturnAndMissingReturn_OnDotnetHost_ReportsError()
        {
            // main(): int 缺 return 与其他非 void 函数一致：必须返回
            var syntaxTree = Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(@"using System

function Main(): i32
{
    Console.WriteLine(""hi"")
}
");
            var compilation = Cocoa.CodeAnalysis.Compilation.Create(syntaxTree);
            var diagnostics = compilation.Emit("e2e-main-int-missing-return", new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, GetOutputPath("e2e-main-int-missing-return"));

            var diagnostic = Assert.Single(diagnostics);
            Assert.Contains("Not all code paths return a value", diagnostic.Message);
        }

        [Fact]
        public void Main_WithNonArrayArgument_OnDotnetHost_ReportsError()
        {
            var syntaxTree = Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(@"
function Main(x: i32)
{
}
");
            var compilation = Cocoa.CodeAnalysis.Compilation.Create(syntaxTree);
            var diagnostics = compilation.Emit("e2e-main-args", new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, GetOutputPath("e2e-main-args"));

            var diagnostic = Assert.Single(diagnostics);
            Assert.Contains("single string[] parameter", diagnostic.Message);
        }

        [Fact]
        public void Main_WithStringArrayArgument_EmitsClean()
        {
            var syntaxTree = Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(@"
function Main(args: string[])
{
}
");
            var compilation = Cocoa.CodeAnalysis.Compilation.Create(syntaxTree);
            var diagnostics = compilation.Emit("e2e-main-args-ok", new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, GetOutputPath("e2e-main-args-ok"));

            Assert.Empty(diagnostics);
        }

        [Fact]
        public void Main_WithBoolReturn_OnDotnetHost_ReportsError()
        {
            var syntaxTree = Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(@"
function Main(): bool
{
    return true
}
");
            var compilation = Cocoa.CodeAnalysis.Compilation.Create(syntaxTree);
            var diagnostics = compilation.Emit("e2e-main-bool-return", new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, GetOutputPath("e2e-main-bool-return"));

            var diagnostic = Assert.Single(diagnostics);
            Assert.Contains("must return either void or int", diagnostic.Message);
        }

        [Fact]
        public void Array_ReadWriteAndLength_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    var a = new i32[3] {10, 20, 30}
    a[1] = 99
    Console.WriteLine(a[0])
    Console.WriteLine(a[1])
    Console.WriteLine(a[2])
    Console.WriteLine(a.Length)
}", "e2e-array");

            Assert.Equal(0, exitCode);
            Assert.Equal("10\r\n99\r\n30\r\n3\r\n", stdout);
        }

        [Fact]
        public void Array_BoolElements_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    var b = new bool[2]
    b[0] = true
    b[1] = false
    Console.WriteLine(b[0])
    Console.WriteLine(b[1])
}", "e2e-array-bool");

            Assert.Equal(0, exitCode);
            Assert.Equal("True\r\nFalse\r\n", stdout);
        }

        [Fact]
        public void Array_OutOfBounds_OnDotnetHost_ExitsNonZero()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    var a = new i32[2]
    a[0] = 1
    a[1] = 2
    Console.WriteLine(a[5])
}", "e2e-array-oob");

            Assert.NotEqual(0, exitCode);
        }

        [Fact]
        public void Array_IndexInLoop_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    var a = new i32[5]
    var i = 0
    while i < 5
    {
        a[i] = i * 10
        i = i + 1
    }
    var sum = 0
    i = 0
    while i < 5
    {
        sum = sum + a[i]
        i = i + 1
    }
    Console.WriteLine(sum)
}", "e2e-array-loop");

            Assert.Equal(0, exitCode);
            Assert.Equal("100\r\n", stdout);
        }

        [Fact]
        public void String_IndexLengthAndSubstring_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    var s = ""hello""
    Console.WriteLine(s.Length)
    Console.WriteLine(s[0])
    Console.WriteLine(i32(s[1]))
    var c = s[2]
    Console.WriteLine(c)
    Console.WriteLine(char(97))
    Console.WriteLine(s.substring(1, 3))
    Console.WriteLine(s.substring(1, 3) + ""!"")
}", "e2e-string-index");

            Assert.Equal(0, exitCode);
            Assert.Equal("5\r\nh\r\n101\r\nl\r\na\r\nell\r\nell!\r\n", stdout);
        }

        [Fact]
        public void CharArray_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    var a = new char[2] {'x', 'y'}
    a[0] = 'z'
    Console.WriteLine(a[0])
    Console.WriteLine(a[1])
}", "e2e-char-array");

            Assert.Equal(0, exitCode);
            Assert.Equal("z\r\ny\r\n", stdout);
        }

        [Fact]
        public void String_IndexOutOfBounds_OnDotnetHost_ExitsNonZero()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    var s = ""abc""
    Console.WriteLine(s[9])
}", "e2e-string-oob");

            Assert.NotEqual(0, exitCode);
        }

        [Fact]
        public void Enum_EndToEnd_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public enum Color { Red, Green, Blue }
public enum HttpStatus { OK = 200, NotFound = 404, InternalServerError = 500 }
function f(c: Color): i32 { return i32(c) }
function Main()
{
    var c = Color.Green
    Console.WriteLine(i32(c))
    Console.WriteLine(i32(HttpStatus.NotFound))
    Console.WriteLine(c == Color.Green)
    Console.WriteLine(c == Color.Red)
    Console.WriteLine(i32(f(Color.Blue)))
    Console.WriteLine(i32(Color(99)) == 99)
}", "e2e-enum");

            Assert.Equal(0, exitCode);
            Assert.Equal("1\r\n404\r\nTrue\r\nFalse\r\n2\r\nTrue\r\n", stdout);
        }

        [Fact]
        public void EnumArray_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public enum Color { Red, Green, Blue }
function Main()
{
    var a = new Color[2] {Color.Red, Color.Green}
    Console.WriteLine(i32(a[0]))
    Console.WriteLine(i32(a[1]))
    a[1] = Color.Blue
    Console.WriteLine(i32(a[1]))
}", "e2e-enum-array");

            Assert.Equal(0, exitCode);
            Assert.Equal("0\r\n1\r\n2\r\n", stdout);
        }

        [Fact]
        public void Byte_EndToEnd_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    var b1: u8 = 65
    Console.WriteLine(b1)
    var buf: u8[] = new u8[3]
    buf[0] = 200
    buf[1] = 0xFF
    Console.WriteLine(buf[0])
    Console.WriteLine(buf[1])
    Console.WriteLine((u8)300)
    Console.WriteLine((i32)buf[0])
    Console.WriteLine((u8)200 == (u8)200)
    Console.WriteLine(0xFF)
}", "e2e-byte");

            Assert.Equal(0, exitCode);
            Assert.Equal("65\r\n200\r\n255\r\n44\r\n200\r\nTrue\r\n255\r\n", stdout);
        }

        [Fact]
        public void Double_EndToEnd_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    var d: f64 = 3.14
    Console.WriteLine(d)
    Console.WriteLine(1.5 + 2.25)
    Console.WriteLine(10.0 / 4)
    Console.WriteLine(2.5 * 2)
    Console.WriteLine(7 - 1.5)
    Console.WriteLine(1.5 < 2.5)
    Console.WriteLine(1.5 == 1.5)
    Console.WriteLine((i32)3.9)
    Console.WriteLine((u8)3.9)
    Console.WriteLine((f64)3)
    var arr: f64[] = new f64[2] {1.5, 2.5}
    arr[0] = 3.5
    Console.WriteLine(arr[0])
    Console.WriteLine(arr[1])
    var sum: f64 = 0.0
    sum = sum + arr[0]
    Console.WriteLine(sum)
}", "e2e-double");

            Assert.Equal(0, exitCode);
            Assert.Equal("3.14\r\n3.75\r\n2.5\r\n5\r\n5.5\r\nTrue\r\nTrue\r\n3\r\n3\r\n3\r\n3.5\r\n2.5\r\n3.5\r\n", stdout);
        }

        [Fact]
        public void String_PlusDouble_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    Console.WriteLine(""d="" + 1.5)
    Console.WriteLine(""x="" + (f64)3)
    Console.WriteLine((string)2.75)
}", "e2e-string-double");

            Assert.Equal(0, exitCode);
            Assert.Equal("d=1.5\r\nx=3\r\n2.75\r\n", stdout);
        }

        [Fact]
        public void Class_Object_Creation_MethodCall_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Point
{
    private _x: i32
    private _y: i32

    public constructor(x: i32, y: i32)
    {
        _x = x
        _y = y
    }

    public function Area(): i32
    {
        return _x * _y
    }

    public function Scale(factor: i32)
    {
        _x = _x * factor
        _y = _y * factor
    }

    public function X(): i32
    {
        return _x
    }

    public function Y(): i32
    {
        return _y
    }
}

function Main()
{
    var p = new Point(3, 4)
    Console.WriteLine(p.Area())
    p.Scale(2)
    Console.WriteLine(p.Area())
    Console.WriteLine(p.X())
    Console.WriteLine(p.Y())
}", "e2e-class-object");

            Assert.Equal(0, exitCode);
            Assert.Equal("12\r\n48\r\n6\r\n8\r\n", stdout);
        }

        [Fact]
        public void Class_SelfMethodCall_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Counter
{
    private _count: i32

    public constructor(start: i32)
    {
        _count = start
    }

    public function Increment()
    {
        _count = _count + 1
    }

    public function Value(): i32
    {
        return _count
    }

    public function Double(): i32
    {
        return Value() * 2
    }
}

function Main()
{
    var c = new Counter(10)
    c.Increment()
    Console.WriteLine(c.Value())
    Console.WriteLine(c.Double())
}", "e2e-class-selfcall");

            Assert.Equal(0, exitCode);
            Assert.Equal("11\r\n22\r\n", stdout);
        }

        [Fact]
        public void Class_TwoInstances_IndependentFields_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Box
{
    private _value: i32

    public constructor(v: i32)
    {
        _value = v
    }

    public function Get(): i32
    {
        return _value
    }
}

function Main()
{
    var a = new Box(1)
    var b = new Box(2)
    Console.WriteLine(a.Get())
    Console.WriteLine(b.Get())
}", "e2e-class-two-instances");

            Assert.Equal(0, exitCode);
            Assert.Equal("1\r\n2\r\n", stdout);
        }

        [Fact]
        public void Class_ExpressionBodiedMethods_CSharpAndCocoaStyle_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Calc
{
    public function Add(a: i32, b: i32): i32 => a + b
    public function Square(x: i32): i32 => x * x
    public function Subtract(a: i32, b: i32): i32 => a - b
    public function Triple(x: i32): i32 => x * 3
}

function Main()
{
    var c = new Calc()
    Console.WriteLine(c.Add(3, 4))
    Console.WriteLine(c.Square(5))
    Console.WriteLine(c.Subtract(10, 4))
    Console.WriteLine(c.Triple(3))
}", "e2e-expression-bodied-methods");

            Assert.Equal(0, exitCode);
            Assert.Equal("7\r\n25\r\n6\r\n9\r\n", stdout);
        }

        [Fact]
        public void Class_ExpressionBodiedProperties_CSharpAndCocoaStyle_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Rect
{
    private _w: i32 = 3
    private _h: i32 = 4

    public property Area: i32 => _w * _h
    public property Width: i32 => _w
    public property DoubleW: i32 => _w * 2
}

function Main()
{
    var r = new Rect()
    Console.WriteLine(r.Area)
    Console.WriteLine(r.Width)
    Console.WriteLine(r.DoubleW)
}", "e2e-expression-bodied-properties");

            Assert.Equal(0, exitCode);
            Assert.Equal("12\r\n3\r\n6\r\n", stdout);
        }

        [Fact]
        public void Class_PrivateSetter_AccessibleWithinClass_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Account
{
    public property Balance: i32 { get private set }

    public function Deposit(amount: i32)
    {
        Balance = Balance + amount
    }
}

function Main()
{
    var a = new Account()
    a.Deposit(100)
    a.Deposit(50)
    Console.WriteLine(a.Balance)
}", "e2e-private-setter");

            Assert.Equal(0, exitCode);
            Assert.Equal("150\r\n", stdout);
        }

        [Fact]
        public void Class_PrivateSetter_NotAccessibleOutside_ReportsDiagnostic()
        {
            var messages = GetEmitDiagnostics(@"
public class Account
{
    public property Balance: i32 { get private set }
}

function Main()
{
    var a = new Account()
    a.Balance = 100
}", "Main");

            Assert.NotEmpty(messages);
            Assert.Contains(messages, m => m.Contains("不能"));
        }

        [Fact]
        public void Oop_Inheritance_Polymorphism_Static_Property_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Shape
{
    private _name: string

    public constructor(name: string)
    {
        _name = name
    }

    public virtual function Describe(): string
    {
        return _name
    }
}

public class Circle extends Shape
{
    private _radius: i32

    public constructor(name: string, radius: i32) extends base(name)
    {
        _radius = radius
    }

    public override function Describe(): string
    {
        return base.Describe() + (string)_radius
    }

    public property Area: i32
    {
        get { return _radius * _radius }
    }
}

public static class MathHelpers
{
    public static function Square(x: i32): i32
    {
        return x * x
    }
}

function Main()
{
    var c = new Circle(""big"", 4)
    Console.WriteLine(c.Describe())
    Console.WriteLine(c.Area)
    Console.WriteLine(MathHelpers.Square(7))
}", "e2e-oop");

            Assert.Equal(0, exitCode);
            Assert.Equal("big4" + "\r\n16\r\n49\r\n", stdout);
        }

        [Fact]
        public void ObjectModel_ObjectFace_Members_And_StaticEquals_OnDotnetHost()
        {
            // 6e-M19 M2-c：System.Object 内建成员面（默认实现 + 静态相等）IL 发射
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Point
{
    private _x: i32

    public constructor(x: i32)
    {
        _x = x
    }
}

function Main()
{
    var p = new Point(1)
    var q = new Point(1)
    Console.WriteLine(Object.Equals(p, p))
    Console.WriteLine(Object.Equals(p, q))
    Console.WriteLine(System.Object.ReferenceEquals(p, p))
    Console.WriteLine(""abc"".ToString())
    Console.WriteLine(42.ToString())
    var s = p.ToString()
    Console.WriteLine(s.Contains(""Point""))
}", "e2e-object-members");

            Assert.Equal(0, exitCode);
            Assert.Equal("True\r\nFalse\r\nTrue\r\nabc\r\n42\r\nTrue\r\n", stdout);
        }

        [Fact]
        public void ObjectModel_ReferenceEquality_ClassTypes_OnDotnetHost()
        {
            // 6e-M19 M2-c：类类型 == / != 引用相等（同引用 True、独立实例 False、基类/派生混合）
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Point
{
    private _x: i32

    public constructor(x: i32)
    {
        _x = x
    }
}

public class Point3D extends Point
{
    public constructor(x: i32) extends base(x)
    {
    }
}

function Main()
{
    var p = new Point(1)
    var q = p
    var r = new Point(1)
    Console.WriteLine(p == q)
    Console.WriteLine(p == r)
    Console.WriteLine(p != q)
    var d = new Point3D(2)
    var o: object = d
    Console.WriteLine(o == d)
    Console.WriteLine(o == p)
}", "e2e-reference-equality");

            Assert.Equal(0, exitCode);
            Assert.Equal("True\r\nFalse\r\nFalse\r\nTrue\r\nFalse\r\n", stdout);
        }

        [Fact]
        public void ObjectModel_Override_ToString_VirtualDispatch_OnDotnetHost()
        {
            // 6e-M19 M2-c/M3-a：用户类 override ToString——经 object 引用调用时 CLR 虚分派到派生实现；
            // base.ToString() 非虚直调基类实现（Animal），不递归
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Animal
{
    public override function ToString(): string
    {
        return ""animal""
    }

    public function DescribeVia(o: object): string
    {
        return ""D="" + o.ToString()
    }
}

public class Dog extends Animal
{
    public override function ToString(): string
    {
        return ""dog("" + base.ToString() + "")""
    }
}

function Main()
{
    var d = new Dog()
    var o: object = d
    Console.WriteLine(o.ToString())
    Console.WriteLine(d.DescribeVia(d))
}", "e2e-override-tostring");

            Assert.Equal(0, exitCode);
            Assert.Equal("dog(animal)\r\nD=dog(animal)\r\n", stdout);
        }

        [Fact]
        public void ObjectModel_Override_GetHashCode_Equals_OnDotnetHost()
        {
            // 6e-M19 M2-c/M3-a：GetHashCode/Equals override 生效（含经 object 引用的槽复用分派）
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Box
{
    private _v: i32

    public constructor(v: i32)
    {
        _v = v
    }

    public override function GetHashCode(): i32
    {
        return _v
    }

    public override function Equals(other: any): bool
    {
        // v1 引用同一性演示 override 生效（null/is/as 随后续里程碑引入）
        return System.Object.ReferenceEquals(other, this)
    }
}

function Main(): i32
{
    var b = new Box(9)
    if b.GetHashCode() != 9 return 1
    var o: object = b
    if o.GetHashCode() != 9 return 2
    if !o.Equals(b) return 3
    return 0
}", "e2e-override-hashcode-equals");

            Assert.Equal(0, exitCode);
            Assert.Equal("", stdout);
        }

        [Fact]
        public void ObjectModel_SystemType_Name_FullName_OnDotnetHost()
        {
            // 6e-M19 M3-b：GetType() → System.Type；Type.Name / FullName / ToString 三成员
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Point
{
}

function Main()
{
    Console.WriteLine(42.GetType().Name)
    var s = ""abc""
    Console.WriteLine(s.GetType().Name)
    var p = new Point()
    Console.WriteLine(p.GetType().Name)
    var t = 42.GetType()
    Console.WriteLine(t.Name == ""Int32"")
    Console.WriteLine(t.FullName)
}", "e2e-system-type");

            Assert.Equal(0, exitCode);
            Assert.Equal("Int32\r\nString\r\nPoint\r\nTrue\r\nSystem.Int32\r\n", stdout);
        }

        [Fact]
        public void Oop_Interface_Implementation_MethodAndProperty_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public interface IShape
{
    function Area(): i32
    property Name: string { get }
}

public class Circle extends IShape
{
    private _radius: i32

    public constructor(radius: i32)
    {
        _radius = radius
    }

    public function Area(): i32
    {
        return _radius * _radius
    }

    public property Name: string
    {
        get { return ""circle"" }
    }
}

function Main()
{
    var s: IShape = new Circle(3)
    Console.WriteLine(s.Area())
    Console.WriteLine(s.Name)
    var c = new Circle(4)
    Console.WriteLine(c.Area())
}", "e2e-interface");

            Assert.Equal(0, exitCode);
            Assert.Equal("9\r\ncircle\r\n16\r\n", stdout);
        }

        [Fact]
        public void Oop_Interface_Inheritance_MultiLevel_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public interface IShape
{
    function Area(): i32
}

public interface IColoredShape extends IShape
{
    function Color(): string
}

public class ColoredSquare extends IColoredShape
{
    private _side: i32

    public constructor(side: i32)
    {
        _side = side
    }

    public function Area(): i32
    {
        return _side * _side
    }

    public function Color(): string
    {
        return ""red""
    }
}

function Main()
{
    var s: IColoredShape = new ColoredSquare(5)
    Console.WriteLine(s.Area())
    Console.WriteLine(s.Color())
    var b: IShape = new ColoredSquare(6)
    Console.WriteLine(b.Area())
}", "e2e-interface-inheritance");

            Assert.Equal(0, exitCode);
            Assert.Equal("25\r\nred\r\n36\r\n", stdout);
        }

        [Fact]
        public void Oop_Interface_BaseChain_Downcast_ParameterReturn_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public interface IAnimal
{
    function Speak(): string
    property Age: i32 { get set }
}

public class Dog extends IAnimal
{
    private _age: i32

    public constructor(age: i32)
    {
        _age = age
    }

    public function Speak(): string
    {
        return ""woof""
    }

    public property Age: i32
    {
        get { return _age }
        set { _age = value }
    }
}

public class Puppy extends Dog
{
    public constructor(age: i32) extends base(age)
    {
    }
}

public function CallSpeak(a: IAnimal): string
{
    return a.Speak()
}

public function MakeAnimal(): IAnimal
{
    return new Dog(3)
}

function Main()
{
    var s: IAnimal = new Puppy(1)
    Console.WriteLine(s.Speak())
    Console.WriteLine(s.Age)
    var d = (Dog)s
    Console.WriteLine(d.Speak())
    d.Age = 7
    Console.WriteLine(d.Age)
    Console.WriteLine(CallSpeak(s))
    var r = MakeAnimal()
    Console.WriteLine(r.Speak())
    var r2: IAnimal = r
    Console.WriteLine(r2.Age)
}", "e2e-interface-basechain");

            Assert.Equal(0, exitCode);
            Assert.Equal("woof\r\n1\r\nwoof\r\n7\r\nwoof\r\nwoof\r\n3\r\n", stdout);
        }

        [Fact]
        public void Oop_Interface_AbstractClass_ImplementsInterface_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public interface IFighter
{
    function Power(): i32
    function Name(): string
}

public abstract class BaseUnit extends IFighter
{
    public function Name(): string
    {
        return ""unit""
    }

    public abstract function Power(): i32
}

public class Knight extends BaseUnit
{
    public function Power(): i32
    {
        return 10
    }
}

public class Archer extends BaseUnit
{
    public function Power(): i32
    {
        return 5
    }
}

function Main()
{
    var k: IFighter = new Knight()
    Console.WriteLine(k.Power())
    Console.WriteLine(k.Name())
    var a: IFighter = new Archer()
    Console.WriteLine(a.Power())
}", "e2e-interface-abstract");

            Assert.Equal(0, exitCode);
            Assert.Equal("10\r\nunit\r\n5\r\n", stdout);
        }

        [Fact]
        public void ExternalInterface_Idisposable_ImplementAndCall_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
using System

public class Resource extends IDisposable
{
    private _name: string

    public constructor(name: string)
    {
        _name = name
    }

    public function Dispose()
    {
        Console.WriteLine(""disposing "" + _name)
    }
}

function Main()
{
    var d: IDisposable = new Resource(""file1"")
    d.Dispose()
    var r = new Resource(""file2"")
    r.Dispose()
}", "e2e-external-idisposable");

            Assert.Equal(0, exitCode);
            Assert.Equal("disposing file1\r\ndisposing file2\r\n", stdout);
        }

        [Fact]
        public void CSStyleFor_PostfixIncrement_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRunCs(@"using System;

public static void Main()
{
    var sum = 0;
    for (var i = 0; i < 5; i++)
    {
        sum = sum + i;
    }
    Console.WriteLine(sum);
    var j = 10;
    j--;
    Console.WriteLine(j);
    j++;
    Console.WriteLine(j);
    var total = 0;
    for (;;)
    {
        total = total + 1;
        if (total == 3)
        {
            break;
        }
    }
    Console.WriteLine(total);
    var k = 0;
    for (; k < 4; k = k + 1)
    {
        if (k == 2)
        {
            continue;
        }
        Console.WriteLine(k);
    }
}", "e2e-cstyle-for");

            Assert.Equal(0, exitCode);
            Assert.Equal("10\r\n9\r\n10\r\n3\r\n0\r\n1\r\n3\r\n", stdout);
        }

        [Fact]
        public void ModuloAndShift_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    Console.WriteLine(7 % 3)
    Console.WriteLine(-7 % 3)
    Console.WriteLine(10 % 2)
    Console.WriteLine(1 << 4)
    Console.WriteLine(8 >> 1)
    Console.WriteLine(-8 >> 1)
    var x = 10
    x %= 3
    Console.WriteLine(x)
    x = 1
    x <<= 4
    Console.WriteLine(x)
    x = -16
    x >>= 2
    Console.WriteLine(x)
    var sum = 0
    for var i = 1 to 5
    {
        if i % 2 == 0
        {
            continue
        }
        sum = sum + i
    }
    Console.WriteLine(sum)
}", "e2e-modulo-shift");

            Assert.Equal(0, exitCode);
            Assert.Equal("1\r\n-1\r\n0\r\n16\r\n4\r\n-4\r\n1\r\n16\r\n-4\r\n9\r\n", stdout);
        }

        [Fact]
        public void ConditionalAndPrefix_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    var a = 5
    var b = 10
    Console.WriteLine(a > b ? a : b)
    Console.WriteLine(1 < 2 ? 3 + 4 : 5 + 6)
    var i = 1
    i = ++i
    Console.WriteLine(i)
    i = --i
    Console.WriteLine(i)
    var d: f64 = 1.5
    d = ++d
    Console.WriteLine(d)
    var n = 7
    Console.WriteLine(n % 2 == 0 ? ""even"" : ""odd"")
    var sum = 0
    for var j = 1 to 5
    {
        sum = sum + (j % 2 == 0 ? 10 : j)
    }
    Console.WriteLine(sum)
}", "e2e-ternary-prefix");

            Assert.Equal(0, exitCode);
            Assert.Equal("10\r\n7\r\n2\r\n1\r\n2.5\r\nodd\r\n29\r\n", stdout);
        }

        [Fact]
        public void Class_ExtendsKeyword_Inheritance_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Animal
{
    private _name: string

    public constructor(name: string)
    {
        _name = name
    }

    public function Name(): string
    {
        return _name
    }
}

public class Dog extends Animal
{
    public constructor(name: string): base(name)
    {
    }

    public function Bark(): string
    {
        return ""woof""
    }
}

function Main()
{
    var d = new Dog(""Rex"")
    Console.WriteLine(d.Name())
    Console.WriteLine(d.Bark())
}", "e2e-extends-keyword");

            Assert.Equal(0, exitCode);
            Assert.Equal("Rex\r\nwoof\r\n", stdout);
        }

        [Fact]
        public void Class_ExtendsKeyword_ConstructorChain_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Animal
{
    private _name: string

    public constructor(name: string)
    {
        _name = name
    }

    public function Name(): string
    {
        return _name
    }
}

public class Dog extends Animal
{
    private _tricks: i32

    public constructor(name: string): base(name)
    {
        _tricks = 0
    }

    public function Tricks(): i32
    {
        return _tricks
    }
}

public class Puppy extends Dog
{
    public constructor(name: string) extends base(name)
    {
    }
}

public class BigPuppy extends Puppy
{
    public constructor(name: string) extends base(name)
    {
    }
}

function Main()
{
    var p = new Puppy(""Rex"")
    Console.WriteLine(p.Name())
    Console.WriteLine(p.Tricks())
    var b = new BigPuppy(""Buddy"")
    Console.WriteLine(b.Name())
    Console.WriteLine(b.Tricks())
}", "e2e-extends-constructor-chain");

            Assert.Equal(0, exitCode);
            Assert.Equal("Rex\r\n0\r\nBuddy\r\n0\r\n", stdout);
        }

        [Fact]
        public void Interface_ExtendsKeyword_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public interface IAnimal
{
    function Speak(): string
}

public interface IDog extends IAnimal
{
    function Bark(): string
}

public class Dog extends IDog
{
    public function Speak(): string
    {
        return ""woof""
    }

    public function Bark(): string
    {
        return ""bark""
    }
}

function Main()
{
    var d: IDog = new Dog()
    Console.WriteLine(d.Speak())
    Console.WriteLine(d.Bark())
}", "e2e-interface-extends");

            Assert.Equal(0, exitCode);
            Assert.Equal("woof\r\nbark\r\n", stdout);
        }

        [Fact]
        public void CSharpStyle_Members_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRunCs(@"using System;

public class Person
{
    private string _name;
    private int _age;
    public static int Count = 0;

    public Person(string name, int age)
    {
        _name = name;
        _age = age;
        Count = Count + 1;
    }

    public string Name { get; set; }

    public int GetAge()
    {
        return _age;
    }
}

public static void Main()
{
    var p = new Person(""Alice"", 30);
    p.Name = ""Bob"";
    Console.WriteLine(p.Name);
    Console.WriteLine(p.GetAge());
    Console.WriteLine(Person.Count);
}", "e2e-cs-style-members");

            Assert.Equal(0, exitCode);
            Assert.Equal("Bob\r\n30\r\n1\r\n", stdout);
        }

        [Fact]
        public void FieldInitializer_Instance_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Counter
{
    private _count: i32 = 5

    public function Get(): i32
    {
        return _count
    }
}

function Main()
{
    var c = new Counter()
    Console.WriteLine(c.Get())
}", "e2e-field-init-instance");

            Assert.Equal(0, exitCode);
            Assert.Equal("5\r\n", stdout);
        }

        [Fact]
        public void FieldInitializer_Static_Cctor_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Config
{
    public static Max: i32 = 100
    public static Base: i32 = 7
}

function Main()
{
    Console.WriteLine(Config.Max + Config.Base)
}", "e2e-field-init-static");

            Assert.Equal(0, exitCode);
            Assert.Equal("107\r\n", stdout);
        }

        [Fact]
        public void FieldInitializer_AutoProperty_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Point
{
    public property X: i32 { get set } = 10
    public property Y: i32 { get set } = 20
}

function Main()
{
    var p = new Point()
    Console.WriteLine(p.X + p.Y)
    p.X = 99
    Console.WriteLine(p.X)
}", "e2e-field-init-autoprop");

            Assert.Equal(0, exitCode);
            Assert.Equal("30\r\n99\r\n", stdout);
        }

        [Fact]
        public void FieldInitializer_Ordering_BaseThenFieldsThenBody_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Trace(tag: string): i32
{
    Base.Text = Base.Text + tag + "";""
    return 0
}

public class Base
{
    public static Text: string = """"

    public constructor()
    {
        Base.Text = Base.Text + ""base;""
    }
}

public class Derived extends Base
{
    private _x: i32 = Trace(""field"")

    public constructor()
    {
        Base.Text = Base.Text + ""body;""
    }
}

function Main()
{
    var d = new Derived()
    Console.WriteLine(Base.Text)
}", "e2e-field-init-ordering");

            Assert.Equal(0, exitCode);
            Assert.Equal("base;field;body;\r\n", stdout);
        }

        [Fact]
        public void FieldInitializer_Static_Ordering_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Counter
{
    public static Start: i32 = 5
    public static End: i32 = Start + 10
}

function Main()
{
    Console.WriteLine(Counter.End)
}", "e2e-field-init-static-ordering");

            Assert.Equal(0, exitCode);
            Assert.Equal("15\r\n", stdout);
        }

        [Fact]
        public void CSharpStyle_LocalVariables_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRunCs(@"using System;

public class Calc
{
    public int Sum(int a, int b)
    {
        int sum = a + b;
        var product = a * b;
        return sum + product;
    }
}

public static void Main()
{
    var c = new Calc();
    Console.WriteLine(c.Sum(2, 3));
}", "e2e-cs-locals");

            Assert.Equal(0, exitCode);
            Assert.Equal("11\r\n", stdout);
        }

        [Fact]
        public void CSharpStyle_TopLevelFunctions_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRunCs(@"using System;

public static void Main()
{
    Console.WriteLine(Add(2, 3));
    Console.WriteLine(Square(4));
    Console.WriteLine(Dup(""hi""));
}

public int Add(int x, int y)
{
    return x + y;
}

public int Square(int n)
{
    return n * n;
}

public string Dup(string s)
{
    return s + s;
}", "e2e-cs-top-level-functions");

            Assert.Equal(0, exitCode);
            Assert.Equal("5\r\n16\r\nhihi\r\n", stdout);
        }

        [Fact]
        public void NoKeyword_TopLevelFunction_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    Console.WriteLine(Add(2, 3))
}

function Add(a: i32, b: i32): i32
{
    return a + b
}", "e2e-no-keyword-top-level");

            Assert.Equal(0, exitCode);
            Assert.Equal("5\r\n", stdout);
        }

        [Fact]
        public void NoKeyword_TopLevelFunction_WithoutReturnType_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    Greet()
}

function Greet()
{
    Console.WriteLine(""hello"")
}", "e2e-no-keyword-top-level-noret");

            Assert.Equal(0, exitCode);
            Assert.Equal("hello\r\n", stdout);
        }

        [Fact]
        public void CSharpStyle_TopLevelFunction_ArrayReturnType_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRunCs(@"using System;

public static void Main()
{
    var nums = GetNums();
    Console.WriteLine(nums.Length);
    Console.WriteLine(nums[0] + nums[1]);
}

public int[] GetNums()
{
    return new int[] { 3, 4 };
}", "e2e-cs-top-level-array-return");

            Assert.Equal(0, exitCode);
            Assert.Equal("2\r\n7\r\n", stdout);
        }

        [Fact]
        public void Entry_QualifiedClassMethod_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Program
{
    public static function Main()
    {
        Console.WriteLine(""hello from class"")
    }
}", "e2e-entry-class", "Program.Main");

            Assert.Equal(0, exitCode);
            Assert.Equal("hello from class\r\n", stdout);
        }

        [Fact]
        public void Entry_NamespaceQualifiedClassMethod_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

namespace My.App
{
    public class Program
    {
        public static function Main()
        {
            Console.WriteLine(""hello from namespace"")
        }
    }
}", "e2e-entry-namespace", "My.App.Program.Main");

            Assert.Equal(0, exitCode);
            Assert.Equal("hello from namespace\r\n", stdout);
        }

        [Fact]
        public void Entry_QualifiedClassMethod_WithArgs_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Program
{
    public static function Main(args: string[])
    {
        Console.WriteLine(args.Length)
        Console.WriteLine(args[0])
    }
}", "e2e-entry-class-args", "Program.Main", processArgs: new[] { "abc" });

            Assert.Equal(0, exitCode);
            Assert.Equal("1\r\nabc\r\n", stdout);
        }

        [Fact]
        public void Entry_SimpleName_UniqueClassStaticMain_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public class App
{
    public static function Main()
    {
        Console.WriteLine(""class main only"")
    }
}", "e2e-entry-class-simple");

            Assert.Equal(0, exitCode);
            Assert.Equal("class main only\r\n", stdout);
        }

        [Fact]
        public void DottedAccess_NamespaceStaticMethod_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

namespace My.App
{
    public class Utils
    {
        public static function Square(x: i32): i32
        {
            return x * x
        }
    }

    public enum Color { Red, Green, Blue }

    public class Config
    {
        public static Version: i32 = 7
    }
}

function Main()
{
    Console.WriteLine(My.App.Utils.Square(4))
    Console.WriteLine(My.App.Config.Version)
    Console.WriteLine(i32(My.App.Color.Green))
}", "e2e-dotted-access");

            Assert.Equal(0, exitCode);
            Assert.Equal("16\r\n7\r\n1\r\n", stdout);
        }

        [Fact]
        public void UsingInternalNamespace_ThenSimpleName_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

namespace Foo.Bar
{
    public class Point
    {
        public function X(): i32
        {
            return 3
        }
    }
}

using Foo.Bar

function Main()
{
    var p = new Point()
    Console.WriteLine(p.X())
}", "e2e-using-internal");

            Assert.Equal(0, exitCode);
            Assert.Equal("3\r\n", stdout);
        }

        [Fact]
        public void TopLevelMain_And_UserClassNamedProgram_OnDotnetHost()
        {
            // 回归：默认容器 TypeDef 改 `<CocoaTopLevel>`，用户 `class Program` 不再撞名
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    Console.WriteLine(Program.X())
}

public class Program
{
    public static function X(): i32
    {
        return 42
    }
}", "e2e-top-level-program-collision");

            Assert.Equal(0, exitCode);
            Assert.Equal("42\r\n", stdout);
        }

        private static string[] GetEmitDiagnostics(string source, string entryPointName)
        {
            var syntaxTree = Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(source);
            var compilation = Cocoa.CodeAnalysis.Compilation.Create(entryPointName, new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, syntaxTree);
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-il-tests", "entry-diag.exe");
            var diagnostics = compilation.Emit("entry-diag", new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, exePath);
            return diagnostics.Select(d => d.Message).ToArray();
        }

        private static string[] GetEmitDiagnosticsCs(string source, string entryPointName)
        {
            var syntaxTree = Cocoa.CodeAnalysis.Syntax.SyntaxTree.ParseCs(source);
            var compilation = Cocoa.CodeAnalysis.Compilation.Create(entryPointName, new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, syntaxTree);
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-il-tests", "entry-diag-cs.exe");
            var diagnostics = compilation.Emit("entry-diag-cs", new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, exePath);
            return diagnostics.Select(d => d.Message).ToArray();
        }

        [Fact]
        public void Entry_QualifiedClassNotFound_Diagnostic()
        {
            var messages = GetEmitDiagnostics("function Main() { }", "Foo.Main");
            Assert.Contains(messages, m => m.Contains("入口函数指定的类 'Foo' 不存在"));
        }

        [Fact]
        public void Entry_QualifiedMethodNotFound_Diagnostic()
        {
            var messages = GetEmitDiagnostics("public class Foo { public static function Bar(): int { return 1 } }", "Foo.Main");
            Assert.Contains(messages, m => m.Contains("类 'Foo' 中不存在静态入口方法 'Main'"));
        }

        [Fact]
        public void Entry_QualifiedMethodNotStatic_Diagnostic()
        {
            var messages = GetEmitDiagnostics("public class Foo { public function Main() { } }", "Foo.Main");
            Assert.Contains(messages, m => m.Contains("类 'Foo' 中不存在静态入口方法 'Main'"));
        }

        [Fact]
        public void Entry_AmbiguousTopLevelAndClassStatic_Diagnostic()
        {
            // 回归：原 SingleOrDefault 崩溃 → 歧义诊断
            var messages = GetEmitDiagnostics(@"using System

function Main() { Console.WriteLine(1) }
public class Foo { public static function Main() { Console.WriteLine(2) } }", "Main");
            Assert.Contains(messages, m => m.Contains("入口函数 'Main' 存在多个匹配"));
        }

        [Fact]
        public void Entry_NamespaceQualifiedClassNotFound_Diagnostic()
        {
            var messages = GetEmitDiagnostics("function Main() { }", "My.App.Program.Main");
            Assert.Contains(messages, m => m.Contains("入口函数指定的类 'My.App.Program' 不存在"));
        }

        [Fact]
        public void CSharpStyleConstLocal_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRunCs(@"using System;

public static void Main()
{
    const int x = 10;
    Console.WriteLine(x);
    const string s = ""hi"";
    Console.WriteLine(s);
    const double d = 3.5;
    Console.WriteLine(d);
}", "e2e-cs-const");

            Assert.Equal(0, exitCode);
            Assert.Equal("10\r\nhi\r\n3.5\r\n", stdout);
        }

        [Fact]
        public void CSharpStyleConstLocal_NotAssignable_ReportsError()
        {
            var messages = GetEmitDiagnosticsCs(@"
public static void Main()
{
    const int x = 10;
    x = 20;
}", "Main");
            Assert.Contains(messages, m => m.Contains("read-only and cannot be assigned"));
        }

        [Fact]
        public void StringEscapes_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(
"function Main()\n" +
"{\n" +
"    Console.WriteLine(\"a\\nb\\tc\\\\d\\\"e\\0f\")\n" +
"    Console.WriteLine(\"\\u0041\\u03A9\")\n" +
"    Console.WriteLine(\"\\U0001F600\".Length)\n" +
"}", "e2e-string-escapes");

            Assert.Equal(0, exitCode);
            Assert.Equal("a\nb\tc\\d\"e\0f\r\nAΩ\r\n2\r\n", stdout);
        }

        [Fact]
        public void VerbatimString_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(
"function Main()\n" +
"{\n" +
"    Console.WriteLine(@\"a\\b\"\"c\")\n" +
"    Console.WriteLine(@\"line1\n" +
"line2\")\n" +
"}", "e2e-verbatim-string");

            Assert.Equal(0, exitCode);
            Assert.Equal("a\\b\"c\r\nline1\nline2\r\n", stdout);
        }

        [Fact]
        public void RawString_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(
"function Main()\n" +
"{\n" +
"    Console.WriteLine(\"\"\"hi\"\"\")\n" +
"    Console.WriteLine(\"\"\"a\"b\"\"\")\n" +
"}", "e2e-raw-string");

            Assert.Equal(0, exitCode);
            Assert.Equal("hi\r\na\"b\r\n", stdout);
        }

        [Fact]
        public void StringInterpolation_Basic_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(
"function Main()\n" +
"{\n" +
"    var name = \"Cocoa\"\n" +
"    Console.WriteLine($\"Hello {name}\")\n" +
"    Console.WriteLine($\"{name}!\")\n" +
"    Console.WriteLine($\"prefix\")\n" +
"}", "e2e-interp-basic");

            Assert.Equal(0, exitCode);
            Assert.Equal("Hello Cocoa\r\nCocoa!\r\nprefix\r\n", stdout);
        }

        [Fact]
        public void StringInterpolation_ExpressionHoles_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(
"function Main()\n" +
"{\n" +
"    var a = 10\n" +
"    var b = 20\n" +
"    Console.WriteLine($\"{a} + {b} = {a + b}\")\n" +
"    Console.WriteLine($\"{a * b}\")\n" +
"    Console.WriteLine($\"{b > a}\")\n" +
"}", "e2e-interp-expr");

            Assert.Equal(0, exitCode);
            Assert.Equal("10 + 20 = 30\r\n200\r\nTrue\r\n", stdout);
        }

        [Fact]
        public void StringInterpolation_TypeConversions_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(
"function Main()\n" +
"{\n" +
"    Console.WriteLine($\"{3.5}\")\n" +
"    Console.WriteLine($\"{true}\")\n" +
"    Console.WriteLine($\"{'A'}\")\n" +
"    var b = 200\n" +
"    Console.WriteLine($\"{b}\")\n" +
"}", "e2e-interp-conversions");

            Assert.Equal(0, exitCode);
            Assert.Equal("3.5\r\nTrue\r\nA\r\n200\r\n", stdout);
        }

        [Fact]
        public void StringInterpolation_EscapedBraces_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(
"function Main()\n" +
"{\n" +
"    var x = 5\n" +
"    Console.WriteLine($\"{{escaped}} {x} {{}}\")\n" +
"}", "e2e-interp-braces");

            Assert.Equal(0, exitCode);
            Assert.Equal("{escaped} 5 {}\r\n", stdout);
        }

        [Fact]
        public void StringInterpolation_VerbatimPrefixes_Multiline_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(
"function Main()\n" +
"{\n" +
"    var x = 7\n" +
"    Console.WriteLine($@\"line1\n" +
"line2 {x}\")\n" +
"    Console.WriteLine(@$\"pre {x}\")\n" +
"}", "e2e-interp-verbatim");

            Assert.Equal(0, exitCode);
            Assert.Equal("line1\nline2 7\r\npre 7\r\n", stdout);
        }

        [Fact]
        public void StringInterpolation_Double_E_Notation_And_FormatDefaults_OnDotnetHost()
        {
            // e-notation 字面量 + E/G 无显式精度默认值（对齐 native 运行时 StringFormat/FormatSci）。
            var (exitCode, stdout) = EmitAndRun(
"function Main()\n" +
"{\n" +
"    Console.WriteLine($\"{1e22:E2}\")\n" +
"    Console.WriteLine($\"{1.5e-3:E2}\")\n" +
"    Console.WriteLine($\"{5e-324:E2}\")\n" +
"    Console.WriteLine($\"{1e308:E2}\")\n" +
"    Console.WriteLine($\"{1.7976931348623157E+308:E}\")\n" +
"    Console.WriteLine($\"{1.7976931348623157E+308:G15}\")\n" +
"    Console.WriteLine($\"{1.0:E}\")\n" +
"    Console.WriteLine($\"{12345.678:E}\")\n" +
"    Console.WriteLine($\"{1.0:G}\")\n" +
"    Console.WriteLine($\"{123456789.0:G}\")\n" +
"    Console.WriteLine($\"{1e22:G}\")\n" +
"    Console.WriteLine($\"{1E-308:G}\")\n" +
"}", "e2e-interp-double-enotation");

            Assert.Equal(0, exitCode);
            Assert.Equal("1.00E+022\r\n" +
                         "1.50E-003\r\n" +
                         "4.94E-324\r\n" +
                         "1.00E+308\r\n" +
                         "1.797693E+308\r\n" +
                         "1.79769313486232E+308\r\n" +
                         "1.000000E+000\r\n" +
                         "1.234568E+004\r\n" +
                         "1\r\n" +
                         "123456789\r\n" +
                         "1E+22\r\n" +
                         "1E-308\r\n", stdout);
        }

        [Fact]
        public void Foreach_OverArrays_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(
"function Main()\n" +
"{\n" +
"    var arr = new i32[] {1, 2, 3}\n" +
"    foreach (var x in arr)\n" +
"    {\n" +
"        Console.WriteLine(x)\n" +
"    }\n" +
"    var sum = 0\n" +
"    foreach (var x in arr)\n" +
"    {\n" +
"        sum = sum + x\n" +
"    }\n" +
"    Console.WriteLine(sum)\n" +
"    var bytes: u8[] = new u8[] {10, 20, 30}\n" +
"    foreach (var b in bytes)\n" +
"    {\n" +
"        Console.WriteLine(b)\n" +
"    }\n" +
"    var doubles: f64[] = new f64[] {1.5, 2.5}\n" +
"    foreach (var d in doubles)\n" +
"    {\n" +
"        Console.WriteLine(d)\n" +
"    }\n" +
"    var names = new string[] {\"a\", \"b\"}\n" +
"    foreach (var n in names)\n" +
"    {\n" +
"        Console.WriteLine(n)\n" +
"    }\n" +
"}", "e2e-foreach-array");

            Assert.Equal(0, exitCode);
            Assert.Equal("1\r\n2\r\n3\r\n6\r\n10\r\n20\r\n30\r\n1.5\r\n2.5\r\na\r\nb\r\n", stdout);
        }

        [Fact]
        public void Foreach_OverString_Chars_And_BreakContinue_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(
"function Main()\n" +
"{\n" +
"    var s = \"abc\"\n" +
"    foreach (var c in s)\n" +
"    {\n" +
"        Console.WriteLine(c)\n" +
"    }\n" +
"    var arr = new i32[] {1, 2, 3, 4}\n" +
"    foreach (var x in arr)\n" +
"    {\n" +
"        if x == 3 continue\n" +
"        if x == 4 break\n" +
"        Console.WriteLine(x)\n" +
"    }\n" +
"    var result = 0\n" +
"    foreach (var i in arr)\n" +
"    {\n" +
"        foreach (var j in arr)\n" +
"        {\n" +
"            if j == 2 continue\n" +
"            result = result + i * j\n" +
"        }\n" +
"    }\n" +
"    Console.WriteLine(result)\n" +
"}", "e2e-foreach-string");

            Assert.Equal(0, exitCode);
            Assert.Equal("a\r\nb\r\nc\r\n1\r\n2\r\n80\r\n", stdout);
        }

        [Fact]
        public void Switch_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(
"function Main()\n" +
"{\n" +
"    var x = 2\n" +
"    switch (x)\n" +
"    {\n" +
"        case 1:\n" +
"        {\n" +
"            Console.WriteLine(\"one\")\n" +
"            break\n" +
"        }\n" +
"        case 2:\n" +
"        {\n" +
"            Console.WriteLine(\"two\")\n" +
"            break\n" +
"        }\n" +
"        default:\n" +
"        {\n" +
"            Console.WriteLine(\"other\")\n" +
"            break\n" +
"        }\n" +
"    }\n" +
"    switch (x)\n" +
"    {\n" +
"        case 1:\n" +
"        case 2:\n" +
"        {\n" +
"            Console.WriteLine(\"low\")\n" +
"            break\n" +
"        }\n" +
"        default:\n" +
"        {\n" +
"            Console.WriteLine(\"high\")\n" +
"            break\n" +
"        }\n" +
"    }\n" +
"    switch (x)\n" +
"    {\n" +
"        case 1:\n" +
"        {\n" +
"            Console.WriteLine(\"one\")\n" +
"            break\n" +
"        }\n" +
"        case 2 when false:\n" +
"        {\n" +
"            Console.WriteLine(\"two-when\")\n" +
"            break\n" +
"        }\n" +
"        default:\n" +
"        {\n" +
"            Console.WriteLine(\"default\")\n" +
"            break\n" +
"        }\n" +
"    }\n" +
"    var s = \"b\"\n" +
"    switch (s)\n" +
"    {\n" +
"        case \"a\":\n" +
"        {\n" +
"            Console.WriteLine(\"A\")\n" +
"            break\n" +
"        }\n" +
"        case \"b\":\n" +
"        {\n" +
"            Console.WriteLine(\"B\")\n" +
"            break\n" +
"        }\n" +
"        default:\n" +
"        {\n" +
"            Console.WriteLine(\"Z\")\n" +
"            break\n" +
"        }\n" +
"    }\n" +
"    var i = 0\n" +
"    var sum = 0\n" +
"    while i < 5\n" +
"    {\n" +
"        switch (i)\n" +
"        {\n" +
"            case 1:\n" +
"            {\n" +
"                i = i + 1\n" +
"                continue\n" +
"            }\n" +
"            case 3:\n" +
"            {\n" +
"                break\n" +
"            }\n" +
"        }\n" +
"        sum = sum + i\n" +
"        i = i + 1\n" +
"    }\n" +
"    Console.WriteLine(sum)\n" +
"}", "e2e-switch");

            Assert.Equal(0, exitCode);
            Assert.Equal("two\r\nlow\r\ndefault\r\nB\r\n9\r\n", stdout);
        }

        [Fact]
        public void Class_MultipleInterfaces_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public interface IShape
{
    function Area(): i32
}

public interface ICloneable
{
    function Clone(): string
}

public class Rectangle extends IShape, ICloneable
{
    private _w: i32
    private _h: i32

    public constructor(w: i32, h: i32)
    {
        _w = w
        _h = h
    }

    public function Area(): i32
    {
        return _w * _h
    }

    public function Clone(): string
    {
        return ""rect""
    }
}

function Main()
{
    var r = new Rectangle(3, 4)
    var s: IShape = r
    var c: ICloneable = r
    Console.WriteLine(s.Area())
    Console.WriteLine(c.Clone())
}", "e2e-multi-interface");

            Assert.Equal(0, exitCode);
            Assert.Equal("12\r\nrect\r\n", stdout);
        }

        [Fact]
        public void Class_BaseClassAndInterface_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Base
{
    public function B(): i32
    {
        return 5
    }
}

public interface IExtra
{
    function X(): i32
}

public class Derived extends Base, IExtra
{
    public function X(): i32
    {
        return 7
    }
}

function Main()
{
    var d = new Derived()
    Console.WriteLine(d.B())
    var e: IExtra = d
    Console.WriteLine(e.X())
}", "e2e-base-plus-interface");

            Assert.Equal(0, exitCode);
            Assert.Equal("5\r\n7\r\n", stdout);
        }

        [Fact]
        public void Class_TwoNonInterfaceBaseTypes_ReportsError()
        {
            var messages = GetEmitDiagnostics(@"
public class A { }
public class B { }
public class C extends A, B { }", "Main");
            Assert.Contains(messages, m => m.Contains("只能有一个非接口基类"));
        }

        [Fact]
        public void Class_StaticConstructor_SetsStaticField_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Config
{
    public static Max: i32

    static constructor()
    {
        Max = 42
    }

    public static function GetMax(): i32
    {
        return Max
    }
}

function Main()
{
    Console.WriteLine(Config.GetMax())
} ", "e2e-static-ctor-csharp-style");

            Assert.Equal(0, exitCode);
            Assert.Equal("42\r\n", stdout);
        }

        [Fact]
        public void Class_StaticConstructor_CocoaStyle_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Config
{
    public static Max: i32

    static constructor()
    {
        Max = 7
    }

    public static function GetMax(): i32
    {
        return Max
    }
}

function Main()
{
    Console.WriteLine(Config.GetMax())
} ", "e2e-static-ctor-cocoa-style");

            Assert.Equal(0, exitCode);
            Assert.Equal("7\r\n", stdout);
        }

        [Fact]
        public void Class_StaticConstructor_FieldInitializerOrdering_OnDotnetHost()
        {
            // C# 语义：静态字段初始化器（按文本序）先于静态构造体执行 → 最终值 2
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Config
{
    public static Order: i32 = 1

    static constructor()
    {
        Order = 2
    }

    public static function GetOrder(): i32
    {
        return Order
    }
}

function Main()
{
    Console.WriteLine(Config.GetOrder())
} ", "e2e-static-ctor-ordering");

            Assert.Equal(0, exitCode);
            Assert.Equal("2\r\n", stdout);
        }

        [Fact]
        public void Class_StaticConstructor_RunsBeforeInstanceConstructor_OnDotnetHost()
        {
            // .cctor 先于首次实例构造触发：实例构造中可读到静态构造写入的静态字段
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Account
{
    public static Seq: i32

    static constructor()
    {
        Seq = 100
    }

    private _base: i32

    public constructor()
    {
        _base = Seq
    }

    public function GetBase(): i32
    {
        return _base
    }
}

function Main()
{
    var a = new Account()
    Console.WriteLine(a.GetBase())
    Console.WriteLine(Account.Seq)
} ", "e2e-static-ctor-before-instance");

            Assert.Equal(0, exitCode);
            Assert.Equal("100\r\n100\r\n", stdout);
        }

        [Fact]
        public void Class_StaticConstructor_WithParameters_ReportsError()
        {
            var messages = GetEmitDiagnostics(@"
public class Foo
{
    static Foo(i32 x)
    {
    }
}", "Main");
            Assert.Contains(messages, m => m.Contains("参数"));
        }

        [Fact]
        public void Class_StaticConstructor_WithChain_ReportsError()
        {
            var messages = GetEmitDiagnostics(@"
public class Base { }
public class Foo extends Base
{
    static constructor() extends base()
    {
    }
}", "Main");
            Assert.Contains(messages, m => m.Contains("构造链"));
        }

        [Fact]
        public void Class_StaticConstructor_ThisAccess_ReportsError()
        {
            var messages = GetEmitDiagnostics(@"
public class Foo
{
    private _x: i32

    static constructor()
    {
        this._x = 1
    }
}", "Main");
            Assert.Contains(messages, m => m.Contains("this"));
        }

        [Fact]
        public void Class_StaticConstructor_InstanceFieldAccess_ReportsError()
        {
            var messages = GetEmitDiagnostics(@"
public class Foo
{
    private _x: i32

    static constructor()
    {
        _x = 1
    }
}", "Main");
            Assert.Contains(messages, m => m.Contains("实例字段"));
        }

        [Fact]
        public void Numeric_Types_Full_Matrix_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    var s: i8 = (i8)-100
    Console.WriteLine(i32(s))
    Console.WriteLine(i32(s * 2))
    var h: i16 = 300
    Console.WriteLine(i32(h * h))
    Console.WriteLine(i32((i16)70000))
    var w: u16 = 60000
    Console.WriteLine(i64(w + w))
    var m: u32 = 4000000000U
    Console.WriteLine(i64(m / 2U))
    Console.WriteLine(m > 3999999999U)
    var un: u32 = 0x80000000U
    Console.WriteLine(i64(un >> 1))
    var sg: i32 = -8
    Console.WriteLine(sg >> 1)
    var big: u64 = 18000000000UL
    Console.WriteLine(big / 3UL)
    var a: f32 = 1.5f
    Console.WriteLine(f64(a * 4.0f))
    Console.WriteLine(f64(-a))
    Console.WriteLine(i32(3.9f))
    Console.WriteLine(f64(f32(2.75)))
}", "numeric-matrix");
            Assert.Equal(0, exitCode);
            Assert.Equal(
                "-100\r\n-200\r\n90000\r\n4464\r\n120000\r\n2000000000\r\nTrue\r\n1073741824\r\n-4\r\n6000000000\r\n6\r\n-1.5\r\n3\r\n2.75\r\n",
                stdout);
        }

        [Fact]
        public void Long_Arithmetic_Bitwise_And_Conversions_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

function Main()
{
    var a = 1000000L
    var b = 7L
    Console.WriteLine(a * b)
    Console.WriteLine(a / b)
    Console.WriteLine(a % b)
    Console.WriteLine(a + b)
    Console.WriteLine(a - b)
    Console.WriteLine(a * a)
    var c = -12345L
    Console.WriteLine(c * -2L)

    var x = 0xFL
    var y = 0x3L
    Console.WriteLine(x & y)
    Console.WriteLine(x | y)
    Console.WriteLine(x ^ y)
    Console.WriteLine(~x)
    var s = 1L
    Console.WriteLine(s << 4)
    Console.WriteLine(s >> 2)

    var i = 5
    var l = 10L
    Console.WriteLine(i + l)
    Console.WriteLine(l - i)
    Console.WriteLine(i * l)
    var big = 123456789012L
    Console.WriteLine((i32)big)
    Console.WriteLine((f64)big)
    Console.WriteLine((i64)(f64)big)
    var d = 123456789012.0
    Console.WriteLine((i64)d)
    Console.WriteLine((i64)3.9)
    Console.WriteLine((i64)-2.9)
    Console.WriteLine((i64)i)
    Console.WriteLine(-big)
    Console.WriteLine(i == l)
    Console.WriteLine(i < l)
}", "il-long");
            Assert.Equal(0, exitCode);
            Assert.Equal(
                "7000000\r\n142857\r\n1\r\n1000007\r\n999993\r\n1000000000000\r\n24690\r\n" +
                "3\r\n15\r\n12\r\n-16\r\n16\r\n0\r\n" +
                "15\r\n5\r\n50\r\n-1097262572\r\n123456789012\r\n123456789012\r\n123456789012\r\n3\r\n-2\r\n5\r\n-123456789012\r\nFalse\r\nTrue\r\n",
                stdout);
        }

        /// <summary>6e-M19 M5-a：null 字面量——引用比较（ceq）/ 赋值转换 / 三元 null 分支 / 空串拼接与打印。</summary>
        [Fact]
        public void Null_Literal_Comparisons_Ternary_Concat_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"using System

public class Box
{
    public function Tag(): string
    {
        return ""b""
    }
}

function Main()
{
    var b = new Box()
    Console.WriteLine(b == null)
    Console.WriteLine(b != null)
    var n: Box = null
    Console.WriteLine(n == null)
    Console.WriteLine(n != null)
    Console.WriteLine(null == null)
    var s: string = null
    Console.WriteLine(s == null)
    Console.WriteLine(s + ""x"")
    Console.WriteLine(s)
    var picked = true ? b : null
    Console.WriteLine(picked == null)
}", "m5a-null-il");

            Assert.Equal(0, exitCode);
            Assert.Equal("False\r\nTrue\r\nTrue\r\nFalse\r\nTrue\r\nTrue\r\nx\r\n\r\nFalse\r\n", stdout);
        }
    }
}
