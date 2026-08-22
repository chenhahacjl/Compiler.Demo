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

        private static (int ExitCode, string Stdout) EmitAndRun(string source, string name, string? input = null)
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
            var (exitCode, stdout) = EmitAndRun(@"
function Main()
{
    var sum = 0
    var i = 0
    while i < 5
    {
        sum = sum + i
        i = i + 1
    }
    System.Console.WriteLine(sum)
    var name = System.Console.ReadLine()
    System.Console.WriteLine(""hello "" + name)
    System.Console.WriteLine(sum > 10)
    var r = System.Runtime.Random(100)
    if r >= 0 && r < 100
    {
        System.Console.WriteLine(""ok"")
    }
}", "e2e-builtins", "World");

            Assert.Equal(0, exitCode);
            Assert.Equal("10\r\nhello World\r\nFalse\r\nok\r\n", stdout);
        }

        [Fact]
        public void Run_SyscallFunction_MemberCall_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
class Runtime
{
    syscall function Random(max: int): int
}

function Main()
{
    var r = Runtime.Random(100)
    if r >= 0 && r < 100
    {
        System.Console.WriteLine(""ok"")
    }
}", "e2e-syscall-member");

            Assert.Equal(0, exitCode);
            Assert.Equal("ok\r\n", stdout);
        }

        [Fact]
        public void Run_SyscallFunction_MemberCall_Print_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
class Runtime
{
    syscall function Print(text: string): void
}

function Main()
{
    Runtime.Print(""hello syscall"")
}", "e2e-syscall-print");

            Assert.Equal(0, exitCode);
            Assert.Equal("hello syscall\r\n", stdout);
        }

        [Fact]
        public void Run_Builtin_SleepNowExit_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function Main()
{
    var t0 = System.Runtime.Now()
    System.Runtime.Sleep(1)
    var t1 = System.Runtime.Now()
    if t1 >= t0
    {
        System.Console.WriteLine(""ok"")
    }
}", "e2e-sleep-now");

            Assert.Equal(0, exitCode);
            Assert.Equal("ok\r\n", stdout);
        }

        [Fact]
        public void Run_Builtin_Exit_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function Main()
{
    System.Runtime.Exit(7)
    System.Console.WriteLine(""unreachable"")
}", "e2e-exit");

            Assert.Equal(7, exitCode);
            Assert.Equal("", stdout);
        }

        [Fact]
        public void Run_DefaultInitializedVariables_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function Main()
{
    var a: int
    var b: bool
    var d: double
    var c: char
    var by: byte
    var s: string
    System.Console.WriteLine(a)
    System.Console.WriteLine(b)
    System.Console.WriteLine(d)
    System.Console.WriteLine(int(c))
    System.Console.WriteLine(int(by))
    System.Console.WriteLine(s == s)
    const x: int = 42
    System.Console.WriteLine(x)
    const y = 7
    System.Console.WriteLine(y + 1)
    var t = x
    t = t + 1
    System.Console.WriteLine(t)
}", "e2e-default-init");

            Assert.Equal(0, exitCode);
            Assert.Equal("0\r\nFalse\r\n0\r\n0\r\n0\r\nTrue\r\n42\r\n8\r\n43\r\n", stdout);
        }

        [Fact]
        public void Run_CocoaProgram_WithUserFunctions_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function add(a: int, b: int): int
{
    return a + b
}

function square(x: int): int
{
    return x * x
}

function greet(name: string): string
{
    return ""Hello, "" + name
}

function fib(n: int): int
{
    if n <= 1
    {
        return n
    }
    return fib(n - 1) + fib(n - 2)
}

function isPositive(n: int): bool
{
    return n > 0
}

function Main()
{
    System.Console.WriteLine(add(2, 3))
    System.Console.WriteLine(square(add(1, 2)))
    System.Console.WriteLine(greet(""Cocoa""))
    System.Console.WriteLine(fib(10))
    System.Console.WriteLine(isPositive(7))
    System.Console.WriteLine(isPositive(0 - 3))
    System.Console.WriteLine(add(fib(6), fib(7)))
}", "e2e-user-functions");

            Assert.Equal(0, exitCode);
            Assert.Equal("5\r\n9\r\nHello, Cocoa\r\n55\r\nTrue\r\nFalse\r\n21\r\n", stdout);
        }

        [Fact]
        public void Run_CocoaProgram_WithPInvoke_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
import kernel32.dll

stdcall function GetTickCount(): int

function Main()
{
    var t = GetTickCount()
    if t > 0
    {
        System.Console.WriteLine(""up"")
    }
}", "e2e-pinvoke");

            Assert.Equal(0, exitCode);
            Assert.Equal("up\r\n", stdout);
        }

        [Fact]
        public void Run_CocoaProgram_WithPInvokeArguments_Stdcall_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
import kernel32.dll

stdcall function ExitProcess(exitCode: int)

function Main()
{
    ExitProcess(42)
}", "e2e-pinvoke-stdcall-args");

            // 退出码 42 证明 int 参数正确穿越 P/Invoke 桩到达 native
            Assert.Equal(42, exitCode);
            Assert.Equal("", stdout);
        }

        [Fact]
        public void Run_CocoaProgram_WithPInvokeArguments_Cdecl_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
import kernel32.dll

cdecl function ExitProcess(exitCode: int)

function Main()
{
    ExitProcess(7)
}", "e2e-pinvoke-cdecl-args");

            // x64 上 cdecl/stdcall 无差异，验证 cdecl 关键字全链路（ImplMap 0x0200 + 参数穿越）
            Assert.Equal(7, exitCode);
            Assert.Equal("", stdout);
        }

        [Fact]
        public void Run_CocoaProgram_WithPInvokeArguments_PointerParam_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
import kernel32.dll

stdcall function GetModuleHandleW(moduleName: int): int

function Main()
{
    var h = GetModuleHandleW(0)
    if h != 0
    {
        System.Console.WriteLine(""ok"")
    }
}", "e2e-pinvoke-pointer-param");

            // int 字面量 0 → LPWSTR(NULL)：模块基址句柄必非 0
            Assert.Equal(0, exitCode);
            Assert.Equal("ok\r\n", stdout);
        }

        [Fact]
        public void Run_CocoaProgram_WithControlFlow_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
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
    System.Console.WriteLine(total)

    var j = 0
    do
    {
        j = j + 1
    } while j < 3
    System.Console.WriteLine(j)

    var m = 0
    for var k = 1 to 10
    {
        if k > 2
        {
            break
        }
        m = m + k
    }
    System.Console.WriteLine(m)

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
    System.Console.WriteLine(nested)
}", "e2e-control-flow");

            Assert.Equal(0, exitCode);
            Assert.Equal("12\r\n3\r\n3\r\n4\r\n", stdout);
        }

        [Fact]
        public void Run_CocoaProgram_WithWideCallAndLongConcat_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function sum10(a: int, b: int, c: int, d: int, e: int, f: int, g: int, h: int, i: int, j: int): int
{
    return a + b + c + d + e + f + g + h + i + j
}

function Main()
{
    let name = ""Cocoa""
    var x = ""1""
    var y = ""2""
    System.Console.WriteLine(""a"" + x + ""b"" + y + ""c"" + name)
    System.Console.WriteLine(sum10(1, 2, 3, 4, 5, 6, 7, 8, 9, 10))
    System.Console.WriteLine(name + ""!"")
}", "e2e-wide-call-long-concat");

            Assert.Equal(0, exitCode);
            Assert.Equal("a1b2cCocoa\r\n55\r\nCocoa!\r\n", stdout);
        }

        [Fact]
        public void Run_CocoaProgram_MainWithIntReturn_OnDotnetHost()
        {
            // main(): int 的返回值成为进程退出码（入口统一为 static int Main()）
            var (exitCode, stdout) = EmitAndRun(@"
function Main(): int
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
            var syntaxTree = Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(@"
function Main(): int
{
    System.Console.WriteLine(""hi"")
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
function Main(x: int)
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
            var (exitCode, stdout) = EmitAndRun(@"
function Main()
{
    var a = new int[3] {10, 20, 30}
    a[1] = 99
    System.Console.WriteLine(a[0])
    System.Console.WriteLine(a[1])
    System.Console.WriteLine(a[2])
    System.Console.WriteLine(a.Length)
}", "e2e-array");

            Assert.Equal(0, exitCode);
            Assert.Equal("10\r\n99\r\n30\r\n3\r\n", stdout);
        }

        [Fact]
        public void Array_BoolElements_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function Main()
{
    var b = new bool[2]
    b[0] = true
    b[1] = false
    System.Console.WriteLine(b[0])
    System.Console.WriteLine(b[1])
}", "e2e-array-bool");

            Assert.Equal(0, exitCode);
            Assert.Equal("True\r\nFalse\r\n", stdout);
        }

        [Fact]
        public void Array_OutOfBounds_OnDotnetHost_ExitsNonZero()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function Main()
{
    var a = new int[2]
    a[0] = 1
    a[1] = 2
    System.Console.WriteLine(a[5])
}", "e2e-array-oob");

            Assert.NotEqual(0, exitCode);
        }

        [Fact]
        public void Array_IndexInLoop_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function Main()
{
    var a = new int[5]
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
    System.Console.WriteLine(sum)
}", "e2e-array-loop");

            Assert.Equal(0, exitCode);
            Assert.Equal("100\r\n", stdout);
        }

        [Fact]
        public void String_IndexLengthAndSubstring_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function Main()
{
    var s = ""hello""
    System.Console.WriteLine(s.Length)
    System.Console.WriteLine(s[0])
    System.Console.WriteLine(int(s[1]))
    var c = s[2]
    System.Console.WriteLine(c)
    System.Console.WriteLine(char(97))
    System.Console.WriteLine(s.substring(1, 3))
    System.Console.WriteLine(s.substring(1, 3) + ""!"")
}", "e2e-string-index");

            Assert.Equal(0, exitCode);
            Assert.Equal("5\r\nh\r\n101\r\nl\r\na\r\nell\r\nell!\r\n", stdout);
        }

        [Fact]
        public void CharArray_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function Main()
{
    var a = new char[2] {'x', 'y'}
    a[0] = 'z'
    System.Console.WriteLine(a[0])
    System.Console.WriteLine(a[1])
}", "e2e-char-array");

            Assert.Equal(0, exitCode);
            Assert.Equal("z\r\ny\r\n", stdout);
        }

        [Fact]
        public void String_IndexOutOfBounds_OnDotnetHost_ExitsNonZero()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function Main()
{
    var s = ""abc""
    System.Console.WriteLine(s[9])
}", "e2e-string-oob");

            Assert.NotEqual(0, exitCode);
        }

        [Fact]
        public void Enum_EndToEnd_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public enum Color { Red, Green, Blue }
public enum HttpStatus { OK = 200, NotFound = 404, InternalServerError = 500 }
function f(c: Color): int { return int(c) }
function Main()
{
    var c = Color.Green
    System.Console.WriteLine(int(c))
    System.Console.WriteLine(int(HttpStatus.NotFound))
    System.Console.WriteLine(c == Color.Green)
    System.Console.WriteLine(c == Color.Red)
    System.Console.WriteLine(int(f(Color.Blue)))
    System.Console.WriteLine(int(Color(99)) == 99)
}", "e2e-enum");

            Assert.Equal(0, exitCode);
            Assert.Equal("1\r\n404\r\nTrue\r\nFalse\r\n2\r\nTrue\r\n", stdout);
        }

        [Fact]
        public void EnumArray_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public enum Color { Red, Green, Blue }
function Main()
{
    var a = new Color[2] {Color.Red, Color.Green}
    System.Console.WriteLine(int(a[0]))
    System.Console.WriteLine(int(a[1]))
    a[1] = Color.Blue
    System.Console.WriteLine(int(a[1]))
}", "e2e-enum-array");

            Assert.Equal(0, exitCode);
            Assert.Equal("0\r\n1\r\n2\r\n", stdout);
        }

        [Fact]
        public void Byte_EndToEnd_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function Main()
{
    var b1: byte = 65
    System.Console.WriteLine(b1)
    var buf: byte[] = new byte[3]
    buf[0] = 200
    buf[1] = 0xFF
    System.Console.WriteLine(buf[0])
    System.Console.WriteLine(buf[1])
    System.Console.WriteLine((byte)300)
    System.Console.WriteLine((int)buf[0])
    System.Console.WriteLine((byte)200 == (byte)200)
    System.Console.WriteLine(0xFF)
}", "e2e-byte");

            Assert.Equal(0, exitCode);
            Assert.Equal("65\r\n200\r\n255\r\n44\r\n200\r\nTrue\r\n255\r\n", stdout);
        }

        [Fact]
        public void Double_EndToEnd_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function Main()
{
    var d: double = 3.14
    System.Console.WriteLine(d)
    System.Console.WriteLine(1.5 + 2.25)
    System.Console.WriteLine(10.0 / 4)
    System.Console.WriteLine(2.5 * 2)
    System.Console.WriteLine(7 - 1.5)
    System.Console.WriteLine(1.5 < 2.5)
    System.Console.WriteLine(1.5 == 1.5)
    System.Console.WriteLine((int)3.9)
    System.Console.WriteLine((byte)3.9)
    System.Console.WriteLine((double)3)
    var arr: double[] = new double[2] {1.5, 2.5}
    arr[0] = 3.5
    System.Console.WriteLine(arr[0])
    System.Console.WriteLine(arr[1])
    var sum: double = 0.0
    sum = sum + arr[0]
    System.Console.WriteLine(sum)
}", "e2e-double");

            Assert.Equal(0, exitCode);
            Assert.Equal("3.14\r\n3.75\r\n2.5\r\n5\r\n5.5\r\nTrue\r\nTrue\r\n3\r\n3\r\n3\r\n3.5\r\n2.5\r\n3.5\r\n", stdout);
        }

        [Fact]
        public void String_PlusDouble_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function Main()
{
    System.Console.WriteLine(""d="" + 1.5)
    System.Console.WriteLine(""x="" + (double)3)
    System.Console.WriteLine((string)2.75)
}", "e2e-string-double");

            Assert.Equal(0, exitCode);
            Assert.Equal("d=1.5\r\nx=3\r\n2.75\r\n", stdout);
        }

        [Fact]
        public void Class_Object_Creation_MethodCall_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public class Point
{
    private _x: int
    private _y: int

    public constructor(x: int, y: int)
    {
        _x = x
        _y = y
    }

    public function Area(): int
    {
        return _x * _y
    }

    public function Scale(factor: int)
    {
        _x = _x * factor
        _y = _y * factor
    }

    public function X(): int
    {
        return _x
    }

    public function Y(): int
    {
        return _y
    }
}

function Main()
{
    var p = new Point(3, 4)
    System.Console.WriteLine(p.Area())
    p.Scale(2)
    System.Console.WriteLine(p.Area())
    System.Console.WriteLine(p.X())
    System.Console.WriteLine(p.Y())
}", "e2e-class-object");

            Assert.Equal(0, exitCode);
            Assert.Equal("12\r\n48\r\n6\r\n8\r\n", stdout);
        }

        [Fact]
        public void Class_SelfMethodCall_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public class Counter
{
    private _count: int

    public constructor(start: int)
    {
        _count = start
    }

    public function Increment()
    {
        _count = _count + 1
    }

    public function Value(): int
    {
        return _count
    }

    public function Double(): int
    {
        return Value() * 2
    }
}

function Main()
{
    var c = new Counter(10)
    c.Increment()
    System.Console.WriteLine(c.Value())
    System.Console.WriteLine(c.Double())
}", "e2e-class-selfcall");

            Assert.Equal(0, exitCode);
            Assert.Equal("11\r\n22\r\n", stdout);
        }

        [Fact]
        public void Class_TwoInstances_IndependentFields_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public class Box
{
    private _value: int

    public constructor(v: int)
    {
        _value = v
    }

    public function Get(): int
    {
        return _value
    }
}

function Main()
{
    var a = new Box(1)
    var b = new Box(2)
    System.Console.WriteLine(a.Get())
    System.Console.WriteLine(b.Get())
}", "e2e-class-two-instances");

            Assert.Equal(0, exitCode);
            Assert.Equal("1\r\n2\r\n", stdout);
        }

        [Fact]
        public void Class_ExpressionBodiedMethods_CSharpAndCocoaStyle_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public class Calc
{
    public function Add(a: int, b: int): int => a + b
    public function Square(x: int): int => x * x
    public function Subtract(a: int, b: int): int => a - b
    public function Triple(x: int): int => x * 3
}

function Main()
{
    var c = new Calc()
    System.Console.WriteLine(c.Add(3, 4))
    System.Console.WriteLine(c.Square(5))
    System.Console.WriteLine(c.Subtract(10, 4))
    System.Console.WriteLine(c.Triple(3))
}", "e2e-expression-bodied-methods");

            Assert.Equal(0, exitCode);
            Assert.Equal("7\r\n25\r\n6\r\n9\r\n", stdout);
        }

        [Fact]
        public void Class_ExpressionBodiedProperties_CSharpAndCocoaStyle_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public class Rect
{
    private _w: int = 3
    private _h: int = 4

    public property Area: int => _w * _h
    public property Width: int => _w
    public property DoubleW: int => _w * 2
}

function Main()
{
    var r = new Rect()
    System.Console.WriteLine(r.Area)
    System.Console.WriteLine(r.Width)
    System.Console.WriteLine(r.DoubleW)
}", "e2e-expression-bodied-properties");

            Assert.Equal(0, exitCode);
            Assert.Equal("12\r\n3\r\n6\r\n", stdout);
        }

        [Fact]
        public void Class_PrivateSetter_AccessibleWithinClass_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public class Account
{
    public property Balance: int { get private set }

    public function Deposit(amount: int)
    {
        Balance = Balance + amount
    }
}

function Main()
{
    var a = new Account()
    a.Deposit(100)
    a.Deposit(50)
    System.Console.WriteLine(a.Balance)
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
    public property Balance: int { get private set }
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
            var (exitCode, stdout) = EmitAndRun(@"
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
    private _radius: int

    public constructor(name: string, radius: int) extends base(name)
    {
        _radius = radius
    }

    public override function Describe(): string
    {
        return base.Describe() + (string)_radius
    }

    public property Area: int
    {
        get { return _radius * _radius }
    }
}

public static class MathHelpers
{
    public static function Square(x: int): int
    {
        return x * x
    }
}

function Main()
{
    var c = new Circle(""big"", 4)
    System.Console.WriteLine(c.Describe())
    System.Console.WriteLine(c.Area)
    System.Console.WriteLine(MathHelpers.Square(7))
}", "e2e-oop");

            Assert.Equal(0, exitCode);
            Assert.Equal("big4" + "\r\n16\r\n49\r\n", stdout);
        }

        [Fact]
        public void Oop_Interface_Implementation_MethodAndProperty_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public interface IShape
{
    function Area(): int
    property Name: string { get }
}

public class Circle extends IShape
{
    private _radius: int

    public constructor(radius: int)
    {
        _radius = radius
    }

    public function Area(): int
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
    System.Console.WriteLine(s.Area())
    System.Console.WriteLine(s.Name)
    var c = new Circle(4)
    System.Console.WriteLine(c.Area())
}", "e2e-interface");

            Assert.Equal(0, exitCode);
            Assert.Equal("9\r\ncircle\r\n16\r\n", stdout);
        }

        [Fact]
        public void Oop_Interface_Inheritance_MultiLevel_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public interface IShape
{
    function Area(): int
}

public interface IColoredShape extends IShape
{
    function Color(): string
}

public class ColoredSquare extends IColoredShape
{
    private _side: int

    public constructor(side: int)
    {
        _side = side
    }

    public function Area(): int
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
    System.Console.WriteLine(s.Area())
    System.Console.WriteLine(s.Color())
    var b: IShape = new ColoredSquare(6)
    System.Console.WriteLine(b.Area())
}", "e2e-interface-inheritance");

            Assert.Equal(0, exitCode);
            Assert.Equal("25\r\nred\r\n36\r\n", stdout);
        }

        [Fact]
        public void Oop_Interface_BaseChain_Downcast_ParameterReturn_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public interface IAnimal
{
    function Speak(): string
    property Age: int { get set }
}

public class Dog extends IAnimal
{
    private _age: int

    public constructor(age: int)
    {
        _age = age
    }

    public function Speak(): string
    {
        return ""woof""
    }

    public property Age: int
    {
        get { return _age }
        set { _age = value }
    }
}

public class Puppy extends Dog
{
    public constructor(age: int) extends base(age)
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
    System.Console.WriteLine(s.Speak())
    System.Console.WriteLine(s.Age)
    var d = (Dog)s
    System.Console.WriteLine(d.Speak())
    d.Age = 7
    System.Console.WriteLine(d.Age)
    System.Console.WriteLine(CallSpeak(s))
    var r = MakeAnimal()
    System.Console.WriteLine(r.Speak())
    var r2: IAnimal = r
    System.Console.WriteLine(r2.Age)
}", "e2e-interface-basechain");

            Assert.Equal(0, exitCode);
            Assert.Equal("woof\r\n1\r\nwoof\r\n7\r\nwoof\r\nwoof\r\n3\r\n", stdout);
        }

        [Fact]
        public void Oop_Interface_AbstractClass_ImplementsInterface_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public interface IFighter
{
    function Power(): int
    function Name(): string
}

public abstract class BaseUnit extends IFighter
{
    public function Name(): string
    {
        return ""unit""
    }

    public abstract function Power(): int
}

public class Knight extends BaseUnit
{
    public function Power(): int
    {
        return 10
    }
}

public class Archer extends BaseUnit
{
    public function Power(): int
    {
        return 5
    }
}

function Main()
{
    var k: IFighter = new Knight()
    System.Console.WriteLine(k.Power())
    System.Console.WriteLine(k.Name())
    var a: IFighter = new Archer()
    System.Console.WriteLine(a.Power())
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
        System.Console.WriteLine(""disposing "" + _name)
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
            var (exitCode, stdout) = EmitAndRunCs(@"
public static void Main()
{
    var sum = 0;
    for (var i = 0; i < 5; i++)
    {
        sum = sum + i;
    }
    System.Console.WriteLine(sum);
    var j = 10;
    j--;
    System.Console.WriteLine(j);
    j++;
    System.Console.WriteLine(j);
    var total = 0;
    for (;;)
    {
        total = total + 1;
        if (total == 3)
        {
            break;
        }
    }
    System.Console.WriteLine(total);
    var k = 0;
    for (; k < 4; k = k + 1)
    {
        if (k == 2)
        {
            continue;
        }
        System.Console.WriteLine(k);
    }
}", "e2e-cstyle-for");

            Assert.Equal(0, exitCode);
            Assert.Equal("10\r\n9\r\n10\r\n3\r\n0\r\n1\r\n3\r\n", stdout);
        }

        [Fact]
        public void ModuloAndShift_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function Main()
{
    System.Console.WriteLine(7 % 3)
    System.Console.WriteLine(-7 % 3)
    System.Console.WriteLine(10 % 2)
    System.Console.WriteLine(1 << 4)
    System.Console.WriteLine(8 >> 1)
    System.Console.WriteLine(-8 >> 1)
    var x = 10
    x %= 3
    System.Console.WriteLine(x)
    x = 1
    x <<= 4
    System.Console.WriteLine(x)
    x = -16
    x >>= 2
    System.Console.WriteLine(x)
    var sum = 0
    for var i = 1 to 5
    {
        if i % 2 == 0
        {
            continue
        }
        sum = sum + i
    }
    System.Console.WriteLine(sum)
}", "e2e-modulo-shift");

            Assert.Equal(0, exitCode);
            Assert.Equal("1\r\n-1\r\n0\r\n16\r\n4\r\n-4\r\n1\r\n16\r\n-4\r\n9\r\n", stdout);
        }

        [Fact]
        public void ConditionalAndPrefix_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function Main()
{
    var a = 5
    var b = 10
    System.Console.WriteLine(a > b ? a : b)
    System.Console.WriteLine(1 < 2 ? 3 + 4 : 5 + 6)
    var i = 1
    i = ++i
    System.Console.WriteLine(i)
    i = --i
    System.Console.WriteLine(i)
    var d: double = 1.5
    d = ++d
    System.Console.WriteLine(d)
    var n = 7
    System.Console.WriteLine(n % 2 == 0 ? ""even"" : ""odd"")
    var sum = 0
    for var j = 1 to 5
    {
        sum = sum + (j % 2 == 0 ? 10 : j)
    }
    System.Console.WriteLine(sum)
}", "e2e-ternary-prefix");

            Assert.Equal(0, exitCode);
            Assert.Equal("10\r\n7\r\n2\r\n1\r\n2.5\r\nodd\r\n29\r\n", stdout);
        }

        [Fact]
        public void Class_ExtendsKeyword_Inheritance_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
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
    System.Console.WriteLine(d.Name())
    System.Console.WriteLine(d.Bark())
}", "e2e-extends-keyword");

            Assert.Equal(0, exitCode);
            Assert.Equal("Rex\r\nwoof\r\n", stdout);
        }

        [Fact]
        public void Class_ExtendsKeyword_ConstructorChain_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
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
    private _tricks: int

    public constructor(name: string): base(name)
    {
        _tricks = 0
    }

    public function Tricks(): int
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
    System.Console.WriteLine(p.Name())
    System.Console.WriteLine(p.Tricks())
    var b = new BigPuppy(""Buddy"")
    System.Console.WriteLine(b.Name())
    System.Console.WriteLine(b.Tricks())
}", "e2e-extends-constructor-chain");

            Assert.Equal(0, exitCode);
            Assert.Equal("Rex\r\n0\r\nBuddy\r\n0\r\n", stdout);
        }

        [Fact]
        public void Interface_ExtendsKeyword_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
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
    System.Console.WriteLine(d.Speak())
    System.Console.WriteLine(d.Bark())
}", "e2e-interface-extends");

            Assert.Equal(0, exitCode);
            Assert.Equal("woof\r\nbark\r\n", stdout);
        }

        [Fact]
        public void CSharpStyle_Members_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRunCs(@"
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
    System.Console.WriteLine(p.Name);
    System.Console.WriteLine(p.GetAge());
    System.Console.WriteLine(Person.Count);
}", "e2e-cs-style-members");

            Assert.Equal(0, exitCode);
            Assert.Equal("Bob\r\n30\r\n1\r\n", stdout);
        }

        [Fact]
        public void FieldInitializer_Instance_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public class Counter
{
    private _count: int = 5

    public function Get(): int
    {
        return _count
    }
}

function Main()
{
    var c = new Counter()
    System.Console.WriteLine(c.Get())
}", "e2e-field-init-instance");

            Assert.Equal(0, exitCode);
            Assert.Equal("5\r\n", stdout);
        }

        [Fact]
        public void FieldInitializer_Static_Cctor_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public class Config
{
    public static Max: int = 100
    public static Base: int = 7
}

function Main()
{
    System.Console.WriteLine(Config.Max + Config.Base)
}", "e2e-field-init-static");

            Assert.Equal(0, exitCode);
            Assert.Equal("107\r\n", stdout);
        }

        [Fact]
        public void FieldInitializer_AutoProperty_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public class Point
{
    public property X: int { get set } = 10
    public property Y: int { get set } = 20
}

function Main()
{
    var p = new Point()
    System.Console.WriteLine(p.X + p.Y)
    p.X = 99
    System.Console.WriteLine(p.X)
}", "e2e-field-init-autoprop");

            Assert.Equal(0, exitCode);
            Assert.Equal("30\r\n99\r\n", stdout);
        }

        [Fact]
        public void FieldInitializer_Ordering_BaseThenFieldsThenBody_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function Trace(tag: string): int
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
    private _x: int = Trace(""field"")

    public constructor()
    {
        Base.Text = Base.Text + ""body;""
    }
}

function Main()
{
    var d = new Derived()
    System.Console.WriteLine(Base.Text)
}", "e2e-field-init-ordering");

            Assert.Equal(0, exitCode);
            Assert.Equal("base;field;body;\r\n", stdout);
        }

        [Fact]
        public void FieldInitializer_Static_Ordering_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public class Counter
{
    public static Start: int = 5
    public static End: int = Start + 10
}

function Main()
{
    System.Console.WriteLine(Counter.End)
}", "e2e-field-init-static-ordering");

            Assert.Equal(0, exitCode);
            Assert.Equal("15\r\n", stdout);
        }

        [Fact]
        public void CSharpStyle_LocalVariables_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRunCs(@"
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
    System.Console.WriteLine(c.Sum(2, 3));
}", "e2e-cs-locals");

            Assert.Equal(0, exitCode);
            Assert.Equal("11\r\n", stdout);
        }

        [Fact]
        public void CSharpStyle_TopLevelFunctions_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRunCs(@"
public static void Main()
{
    System.Console.WriteLine(Add(2, 3));
    System.Console.WriteLine(Square(4));
    System.Console.WriteLine(Double(""hi""));
}

public int Add(int x, int y)
{
    return x + y;
}

public int Square(int n)
{
    return n * n;
}

public string Double(string s)
{
    return s + s;
}", "e2e-cs-top-level-functions");

            Assert.Equal(0, exitCode);
            Assert.Equal("5\r\n16\r\nhihi\r\n", stdout);
        }

        [Fact]
        public void NoKeyword_TopLevelFunction_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function Main()
{
    System.Console.WriteLine(Add(2, 3))
}

function Add(a: int, b: int): int
{
    return a + b
}", "e2e-no-keyword-top-level");

            Assert.Equal(0, exitCode);
            Assert.Equal("5\r\n", stdout);
        }

        [Fact]
        public void NoKeyword_TopLevelFunction_WithoutReturnType_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function Main()
{
    Greet()
}

function Greet()
{
    System.Console.WriteLine(""hello"")
}", "e2e-no-keyword-top-level-noret");

            Assert.Equal(0, exitCode);
            Assert.Equal("hello\r\n", stdout);
        }

        [Fact]
        public void CSharpStyle_TopLevelFunction_ArrayReturnType_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRunCs(@"
public static void Main()
{
    var nums = GetNums();
    System.Console.WriteLine(nums.Length);
    System.Console.WriteLine(nums[0] + nums[1]);
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
            var (exitCode, stdout) = EmitAndRun(@"
public class Program
{
    public static function Main()
    {
        System.Console.WriteLine(""hello from class"")
    }
}", "e2e-entry-class", "Program.Main");

            Assert.Equal(0, exitCode);
            Assert.Equal("hello from class\r\n", stdout);
        }

        [Fact]
        public void Entry_NamespaceQualifiedClassMethod_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
namespace My.App
{
    public class Program
    {
        public static function Main()
        {
            System.Console.WriteLine(""hello from namespace"")
        }
    }
}", "e2e-entry-namespace", "My.App.Program.Main");

            Assert.Equal(0, exitCode);
            Assert.Equal("hello from namespace\r\n", stdout);
        }

        [Fact]
        public void Entry_QualifiedClassMethod_WithArgs_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public class Program
{
    public static function Main(args: string[])
    {
        System.Console.WriteLine(args.Length)
        System.Console.WriteLine(args[0])
    }
}", "e2e-entry-class-args", "Program.Main", processArgs: new[] { "abc" });

            Assert.Equal(0, exitCode);
            Assert.Equal("1\r\nabc\r\n", stdout);
        }

        [Fact]
        public void Entry_SimpleName_UniqueClassStaticMain_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public class App
{
    public static function Main()
    {
        System.Console.WriteLine(""class main only"")
    }
}", "e2e-entry-class-simple");

            Assert.Equal(0, exitCode);
            Assert.Equal("class main only\r\n", stdout);
        }

        [Fact]
        public void DottedAccess_NamespaceStaticMethod_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
namespace My.App
{
    public class Utils
    {
        public static function Square(x: int): int
        {
            return x * x
        }
    }

    public enum Color { Red, Green, Blue }

    public class Config
    {
        public static Version: int = 7
    }
}

function Main()
{
    System.Console.WriteLine(My.App.Utils.Square(4))
    System.Console.WriteLine(My.App.Config.Version)
    System.Console.WriteLine(int(My.App.Color.Green))
}", "e2e-dotted-access");

            Assert.Equal(0, exitCode);
            Assert.Equal("16\r\n7\r\n1\r\n", stdout);
        }

        [Fact]
        public void UsingInternalNamespace_ThenSimpleName_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
namespace Foo.Bar
{
    public class Point
    {
        public function X(): int
        {
            return 3
        }
    }
}

using Foo.Bar

function Main()
{
    var p = new Point()
    System.Console.WriteLine(p.X())
}", "e2e-using-internal");

            Assert.Equal(0, exitCode);
            Assert.Equal("3\r\n", stdout);
        }

        [Fact]
        public void TopLevelMain_And_UserClassNamedProgram_OnDotnetHost()
        {
            // 回归：默认容器 TypeDef 改 `<CocoaTopLevel>`，用户 `class Program` 不再撞名
            var (exitCode, stdout) = EmitAndRun(@"
function Main()
{
    System.Console.WriteLine(Program.X())
}

public class Program
{
    public static function X(): int
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
            var messages = GetEmitDiagnostics(@"
function Main() { System.Console.WriteLine(1) }
public class Foo { public static function Main() { System.Console.WriteLine(2) } }", "Main");
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
            var (exitCode, stdout) = EmitAndRunCs(@"
public static void Main()
{
    const int x = 10;
    System.Console.WriteLine(x);
    const string s = ""hi"";
    System.Console.WriteLine(s);
    const double d = 3.5;
    System.Console.WriteLine(d);
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
"    System.Console.WriteLine(\"a\\nb\\tc\\\\d\\\"e\\0f\")\n" +
"    System.Console.WriteLine(\"\\u0041\\u03A9\")\n" +
"    System.Console.WriteLine(\"\\U0001F600\".Length)\n" +
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
"    System.Console.WriteLine(@\"a\\b\"\"c\")\n" +
"    System.Console.WriteLine(@\"line1\n" +
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
"    System.Console.WriteLine(\"\"\"hi\"\"\")\n" +
"    System.Console.WriteLine(\"\"\"a\"b\"\"\")\n" +
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
"    System.Console.WriteLine($\"Hello {name}\")\n" +
"    System.Console.WriteLine($\"{name}!\")\n" +
"    System.Console.WriteLine($\"prefix\")\n" +
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
"    System.Console.WriteLine($\"{a} + {b} = {a + b}\")\n" +
"    System.Console.WriteLine($\"{a * b}\")\n" +
"    System.Console.WriteLine($\"{b > a}\")\n" +
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
"    System.Console.WriteLine($\"{3.5}\")\n" +
"    System.Console.WriteLine($\"{true}\")\n" +
"    System.Console.WriteLine($\"{'A'}\")\n" +
"    var b = 200\n" +
"    System.Console.WriteLine($\"{b}\")\n" +
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
"    System.Console.WriteLine($\"{{escaped}} {x} {{}}\")\n" +
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
"    System.Console.WriteLine($@\"line1\n" +
"line2 {x}\")\n" +
"    System.Console.WriteLine(@$\"pre {x}\")\n" +
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
"    System.Console.WriteLine($\"{1e22:E2}\")\n" +
"    System.Console.WriteLine($\"{1.5e-3:E2}\")\n" +
"    System.Console.WriteLine($\"{5e-324:E2}\")\n" +
"    System.Console.WriteLine($\"{1e308:E2}\")\n" +
"    System.Console.WriteLine($\"{1.7976931348623157E+308:E}\")\n" +
"    System.Console.WriteLine($\"{1.7976931348623157E+308:G15}\")\n" +
"    System.Console.WriteLine($\"{1.0:E}\")\n" +
"    System.Console.WriteLine($\"{12345.678:E}\")\n" +
"    System.Console.WriteLine($\"{1.0:G}\")\n" +
"    System.Console.WriteLine($\"{123456789.0:G}\")\n" +
"    System.Console.WriteLine($\"{1e22:G}\")\n" +
"    System.Console.WriteLine($\"{1E-308:G}\")\n" +
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
"    var arr = new int[] {1, 2, 3}\n" +
"    foreach (var x in arr)\n" +
"    {\n" +
"        System.Console.WriteLine(x)\n" +
"    }\n" +
"    var sum = 0\n" +
"    foreach (var x in arr)\n" +
"    {\n" +
"        sum = sum + x\n" +
"    }\n" +
"    System.Console.WriteLine(sum)\n" +
"    var bytes: byte[] = new byte[] {10, 20, 30}\n" +
"    foreach (var b in bytes)\n" +
"    {\n" +
"        System.Console.WriteLine(b)\n" +
"    }\n" +
"    var doubles: double[] = new double[] {1.5, 2.5}\n" +
"    foreach (var d in doubles)\n" +
"    {\n" +
"        System.Console.WriteLine(d)\n" +
"    }\n" +
"    var names = new string[] {\"a\", \"b\"}\n" +
"    foreach (var n in names)\n" +
"    {\n" +
"        System.Console.WriteLine(n)\n" +
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
"        System.Console.WriteLine(c)\n" +
"    }\n" +
"    var arr = new int[] {1, 2, 3, 4}\n" +
"    foreach (var x in arr)\n" +
"    {\n" +
"        if x == 3 continue\n" +
"        if x == 4 break\n" +
"        System.Console.WriteLine(x)\n" +
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
"    System.Console.WriteLine(result)\n" +
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
"            System.Console.WriteLine(\"one\")\n" +
"            break\n" +
"        }\n" +
"        case 2:\n" +
"        {\n" +
"            System.Console.WriteLine(\"two\")\n" +
"            break\n" +
"        }\n" +
"        default:\n" +
"        {\n" +
"            System.Console.WriteLine(\"other\")\n" +
"            break\n" +
"        }\n" +
"    }\n" +
"    switch (x)\n" +
"    {\n" +
"        case 1:\n" +
"        case 2:\n" +
"        {\n" +
"            System.Console.WriteLine(\"low\")\n" +
"            break\n" +
"        }\n" +
"        default:\n" +
"        {\n" +
"            System.Console.WriteLine(\"high\")\n" +
"            break\n" +
"        }\n" +
"    }\n" +
"    switch (x)\n" +
"    {\n" +
"        case 1:\n" +
"        {\n" +
"            System.Console.WriteLine(\"one\")\n" +
"            break\n" +
"        }\n" +
"        case 2 when false:\n" +
"        {\n" +
"            System.Console.WriteLine(\"two-when\")\n" +
"            break\n" +
"        }\n" +
"        default:\n" +
"        {\n" +
"            System.Console.WriteLine(\"default\")\n" +
"            break\n" +
"        }\n" +
"    }\n" +
"    var s = \"b\"\n" +
"    switch (s)\n" +
"    {\n" +
"        case \"a\":\n" +
"        {\n" +
"            System.Console.WriteLine(\"A\")\n" +
"            break\n" +
"        }\n" +
"        case \"b\":\n" +
"        {\n" +
"            System.Console.WriteLine(\"B\")\n" +
"            break\n" +
"        }\n" +
"        default:\n" +
"        {\n" +
"            System.Console.WriteLine(\"Z\")\n" +
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
"    System.Console.WriteLine(sum)\n" +
"}", "e2e-switch");

            Assert.Equal(0, exitCode);
            Assert.Equal("two\r\nlow\r\ndefault\r\nB\r\n9\r\n", stdout);
        }

        [Fact]
        public void Class_MultipleInterfaces_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public interface IShape
{
    function Area(): int
}

public interface ICloneable
{
    function Clone(): string
}

public class Rectangle extends IShape, ICloneable
{
    private _w: int
    private _h: int

    public constructor(w: int, h: int)
    {
        _w = w
        _h = h
    }

    public function Area(): int
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
    System.Console.WriteLine(s.Area())
    System.Console.WriteLine(c.Clone())
}", "e2e-multi-interface");

            Assert.Equal(0, exitCode);
            Assert.Equal("12\r\nrect\r\n", stdout);
        }

        [Fact]
        public void Class_BaseClassAndInterface_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public class Base
{
    public function B(): int
    {
        return 5
    }
}

public interface IExtra
{
    function X(): int
}

public class Derived extends Base, IExtra
{
    public function X(): int
    {
        return 7
    }
}

function Main()
{
    var d = new Derived()
    System.Console.WriteLine(d.B())
    var e: IExtra = d
    System.Console.WriteLine(e.X())
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
            var (exitCode, stdout) = EmitAndRun(@"
public class Config
{
    public static Max: int

    static constructor()
    {
        Max = 42
    }

    public static function GetMax(): int
    {
        return Max
    }
}

function Main()
{
    System.Console.WriteLine(Config.GetMax())
} ", "e2e-static-ctor-csharp-style");

            Assert.Equal(0, exitCode);
            Assert.Equal("42\r\n", stdout);
        }

        [Fact]
        public void Class_StaticConstructor_CocoaStyle_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
public class Config
{
    public static Max: int

    static constructor()
    {
        Max = 7
    }

    public static function GetMax(): int
    {
        return Max
    }
}

function Main()
{
    System.Console.WriteLine(Config.GetMax())
} ", "e2e-static-ctor-cocoa-style");

            Assert.Equal(0, exitCode);
            Assert.Equal("7\r\n", stdout);
        }

        [Fact]
        public void Class_StaticConstructor_FieldInitializerOrdering_OnDotnetHost()
        {
            // C# 语义：静态字段初始化器（按文本序）先于静态构造体执行 → 最终值 2
            var (exitCode, stdout) = EmitAndRun(@"
public class Config
{
    public static Order: int = 1

    static constructor()
    {
        Order = 2
    }

    public static function GetOrder(): int
    {
        return Order
    }
}

function Main()
{
    System.Console.WriteLine(Config.GetOrder())
} ", "e2e-static-ctor-ordering");

            Assert.Equal(0, exitCode);
            Assert.Equal("2\r\n", stdout);
        }

        [Fact]
        public void Class_StaticConstructor_RunsBeforeInstanceConstructor_OnDotnetHost()
        {
            // .cctor 先于首次实例构造触发：实例构造中可读到静态构造写入的静态字段
            var (exitCode, stdout) = EmitAndRun(@"
public class Account
{
    public static Seq: int

    static constructor()
    {
        Seq = 100
    }

    private _base: int

    public constructor()
    {
        _base = Seq
    }

    public function GetBase(): int
    {
        return _base
    }
}

function Main()
{
    var a = new Account()
    System.Console.WriteLine(a.GetBase())
    System.Console.WriteLine(Account.Seq)
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
    static Foo(int x)
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
    private _x: int

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
    private _x: int

    static constructor()
    {
        _x = 1
    }
}", "Main");
            Assert.Contains(messages, m => m.Contains("实例字段"));
        }
    }
}
