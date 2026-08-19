using System;
using System.Diagnostics;
using System.IO;
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
        {
            var syntaxTree = Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(source);
            var compilation = Cocoa.CodeAnalysis.Compilation.Create(syntaxTree);
            var exePath = GetOutputPath(name);
            var diagnostics = compilation.Emit(name, new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, exePath);

            Assert.Empty(diagnostics);
            Assert.True(File.Exists(exePath));

            var psi = new ProcessStartInfo("dotnet", $"\"{exePath}\"")
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
    print(sum)
    var name = input()
    print(""hello "" + name)
    print(sum > 10)
    var r = random(100)
    if r >= 0 && r < 100
    {
        print(""ok"")
    }
}", "e2e-builtins", "World");

            Assert.Equal(0, exitCode);
            Assert.Equal("10\r\nhello World\r\nFalse\r\nok\r\n", stdout);
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
    print(add(2, 3))
    print(square(add(1, 2)))
    print(greet(""Cocoa""))
    print(fib(10))
    print(isPositive(7))
    print(isPositive(0 - 3))
    print(add(fib(6), fib(7)))
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
        print(""up"")
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
        print(""ok"")
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
    for i = 1 to 5
    {
        if i == 3
        {
            continue
        }
        total = total + i
    }
    print(total)

    var j = 0
    do
    {
        j = j + 1
    } while j < 3
    print(j)

    var m = 0
    for k = 1 to 10
    {
        if k > 2
        {
            break
        }
        m = m + k
    }
    print(m)

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
    print(nested)
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
    print(""a"" + x + ""b"" + y + ""c"" + name)
    print(sum10(1, 2, 3, 4, 5, 6, 7, 8, 9, 10))
    print(name + ""!"")
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
    print(""hi"")
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
    print(a[0])
    print(a[1])
    print(a[2])
    print(a.Length)
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
    print(b[0])
    print(b[1])
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
    print(a[5])
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
    print(sum)
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
    print(s.Length)
    print(s[0])
    print(int(s[1]))
    var c = s[2]
    print(c)
    print(char(97))
    print(s.substring(1, 3))
    print(s.substring(1, 3) + ""!"")
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
    print(a[0])
    print(a[1])
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
    print(s[9])
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
    print(int(c))
    print(int(HttpStatus.NotFound))
    print(c == Color.Green)
    print(c == Color.Red)
    print(int(f(Color.Blue)))
    print(int(Color(99)) == 99)
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
    print(int(a[0]))
    print(int(a[1]))
    a[1] = Color.Blue
    print(int(a[1]))
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
    print(b1)
    var buf: byte[] = new byte[3]
    buf[0] = 200
    buf[1] = 0xFF
    print(buf[0])
    print(buf[1])
    print((byte)300)
    print((int)buf[0])
    print((byte)200 == (byte)200)
    print(0xFF)
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
    print(d)
    print(1.5 + 2.25)
    print(10.0 / 4)
    print(2.5 * 2)
    print(7 - 1.5)
    print(1.5 < 2.5)
    print(1.5 == 1.5)
    print((int)3.9)
    print((byte)3.9)
    print((double)3)
    var arr: double[] = new double[2] {1.5, 2.5}
    arr[0] = 3.5
    print(arr[0])
    print(arr[1])
    var sum: double = 0.0
    sum = sum + arr[0]
    print(sum)
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
    print(""d="" + 1.5)
    print(""x="" + (double)3)
    print((string)2.75)
}", "e2e-string-double");

            Assert.Equal(0, exitCode);
            Assert.Equal("d=1.5\r\nx=3\r\n2.75\r\n", stdout);
        }
    }
}
