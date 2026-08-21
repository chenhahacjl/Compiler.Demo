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
    print(a)
    print(b)
    print(d)
    print(int(c))
    print(int(by))
    print(s == s)
    const x: int = 42
    print(x)
    const y = 7
    print(y + 1)
    var t = x
    t = t + 1
    print(t)
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
    for var i = 1 to 5
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
    for var k = 1 to 10
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
    print(p.Area())
    p.Scale(2)
    print(p.Area())
    print(p.X())
    print(p.Y())
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
    print(c.Value())
    print(c.Double())
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
    print(a.Get())
    print(b.Get())
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
    print(c.Add(3, 4))
    print(c.Square(5))
    print(c.Subtract(10, 4))
    print(c.Triple(3))
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
    print(r.Area)
    print(r.Width)
    print(r.DoubleW)
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
    print(a.Balance)
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
    print(c.Describe())
    print(c.Area)
    print(MathHelpers.Square(7))
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
    print(s.Area())
    print(s.Name)
    var c = new Circle(4)
    print(c.Area())
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
    print(s.Area())
    print(s.Color())
    var b: IShape = new ColoredSquare(6)
    print(b.Area())
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
    print(s.Speak())
    print(s.Age)
    var d = (Dog)s
    print(d.Speak())
    d.Age = 7
    print(d.Age)
    print(CallSpeak(s))
    var r = MakeAnimal()
    print(r.Speak())
    var r2: IAnimal = r
    print(r2.Age)
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
    print(k.Power())
    print(k.Name())
    var a: IFighter = new Archer()
    print(a.Power())
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
        print(""disposing "" + _name)
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
    print(sum);
    var j = 10;
    j--;
    print(j);
    j++;
    print(j);
    var total = 0;
    for (;;)
    {
        total = total + 1;
        if (total == 3)
        {
            break;
        }
    }
    print(total);
    var k = 0;
    for (; k < 4; k = k + 1)
    {
        if (k == 2)
        {
            continue;
        }
        print(k);
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
    print(7 % 3)
    print(-7 % 3)
    print(10 % 2)
    print(1 << 4)
    print(8 >> 1)
    print(-8 >> 1)
    var x = 10
    x %= 3
    print(x)
    x = 1
    x <<= 4
    print(x)
    x = -16
    x >>= 2
    print(x)
    var sum = 0
    for var i = 1 to 5
    {
        if i % 2 == 0
        {
            continue
        }
        sum = sum + i
    }
    print(sum)
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
    print(a > b ? a : b)
    print(1 < 2 ? 3 + 4 : 5 + 6)
    var i = 1
    i = ++i
    print(i)
    i = --i
    print(i)
    var d: double = 1.5
    d = ++d
    print(d)
    var n = 7
    print(n % 2 == 0 ? ""even"" : ""odd"")
    var sum = 0
    for var j = 1 to 5
    {
        sum = sum + (j % 2 == 0 ? 10 : j)
    }
    print(sum)
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
    print(d.Name())
    print(d.Bark())
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
    print(p.Name())
    print(p.Tricks())
    var b = new BigPuppy(""Buddy"")
    print(b.Name())
    print(b.Tricks())
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
    print(d.Speak())
    print(d.Bark())
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
    print(p.Name);
    print(p.GetAge());
    print(Person.Count);
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
    print(c.Get())
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
    print(Config.Max + Config.Base)
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
    print(p.X + p.Y)
    p.X = 99
    print(p.X)
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
    print(Base.Text)
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
    print(Counter.End)
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
    print(c.Sum(2, 3));
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
    print(Add(2, 3));
    print(Square(4));
    print(Double(""hi""));
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
    print(Add(2, 3))
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
    print(""hello"")
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
    print(nums.Length);
    print(nums[0] + nums[1]);
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
        print(""hello from class"")
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
            print(""hello from namespace"")
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
        print(args.Length)
        print(args[0])
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
        print(""class main only"")
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
    print(My.App.Utils.Square(4))
    print(My.App.Config.Version)
    print(int(My.App.Color.Green))
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
    print(p.X())
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
    print(Program.X())
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
function Main() { print(1) }
public class Foo { public static function Main() { print(2) } }", "Main");
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
    print(x);
    const string s = ""hi"";
    print(s);
    const double d = 3.5;
    print(d);
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
"    print(\"a\\nb\\tc\\\\d\\\"e\\0f\")\n" +
"    print(\"\\u0041\\u03A9\")\n" +
"    print(\"\\U0001F600\".Length)\n" +
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
"    print(@\"a\\b\"\"c\")\n" +
"    print(@\"line1\n" +
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
"    print(\"\"\"hi\"\"\")\n" +
"    print(\"\"\"a\"b\"\"\")\n" +
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
"    print($\"Hello {name}\")\n" +
"    print($\"{name}!\")\n" +
"    print($\"prefix\")\n" +
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
"    print($\"{a} + {b} = {a + b}\")\n" +
"    print($\"{a * b}\")\n" +
"    print($\"{b > a}\")\n" +
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
"    print($\"{3.5}\")\n" +
"    print($\"{true}\")\n" +
"    print($\"{'A'}\")\n" +
"    var b = 200\n" +
"    print($\"{b}\")\n" +
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
"    print($\"{{escaped}} {x} {{}}\")\n" +
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
"    print($@\"line1\n" +
"line2 {x}\")\n" +
"    print(@$\"pre {x}\")\n" +
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
"    print($\"{1e22:E2}\")\n" +
"    print($\"{1.5e-3:E2}\")\n" +
"    print($\"{5e-324:E2}\")\n" +
"    print($\"{1e308:E2}\")\n" +
"    print($\"{1.7976931348623157E+308:E}\")\n" +
"    print($\"{1.7976931348623157E+308:G15}\")\n" +
"    print($\"{1.0:E}\")\n" +
"    print($\"{12345.678:E}\")\n" +
"    print($\"{1.0:G}\")\n" +
"    print($\"{123456789.0:G}\")\n" +
"    print($\"{1e22:G}\")\n" +
"    print($\"{1E-308:G}\")\n" +
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
"        print(x)\n" +
"    }\n" +
"    var sum = 0\n" +
"    foreach (var x in arr)\n" +
"    {\n" +
"        sum = sum + x\n" +
"    }\n" +
"    print(sum)\n" +
"    var bytes: byte[] = new byte[] {10, 20, 30}\n" +
"    foreach (var b in bytes)\n" +
"    {\n" +
"        print(b)\n" +
"    }\n" +
"    var doubles: double[] = new double[] {1.5, 2.5}\n" +
"    foreach (var d in doubles)\n" +
"    {\n" +
"        print(d)\n" +
"    }\n" +
"    var names = new string[] {\"a\", \"b\"}\n" +
"    foreach (var n in names)\n" +
"    {\n" +
"        print(n)\n" +
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
"        print(c)\n" +
"    }\n" +
"    var arr = new int[] {1, 2, 3, 4}\n" +
"    foreach (var x in arr)\n" +
"    {\n" +
"        if x == 3 continue\n" +
"        if x == 4 break\n" +
"        print(x)\n" +
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
"    print(result)\n" +
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
"            print(\"one\")\n" +
"            break\n" +
"        }\n" +
"        case 2:\n" +
"        {\n" +
"            print(\"two\")\n" +
"            break\n" +
"        }\n" +
"        default:\n" +
"        {\n" +
"            print(\"other\")\n" +
"            break\n" +
"        }\n" +
"    }\n" +
"    switch (x)\n" +
"    {\n" +
"        case 1:\n" +
"        case 2:\n" +
"        {\n" +
"            print(\"low\")\n" +
"            break\n" +
"        }\n" +
"        default:\n" +
"        {\n" +
"            print(\"high\")\n" +
"            break\n" +
"        }\n" +
"    }\n" +
"    switch (x)\n" +
"    {\n" +
"        case 1:\n" +
"        {\n" +
"            print(\"one\")\n" +
"            break\n" +
"        }\n" +
"        case 2 when false:\n" +
"        {\n" +
"            print(\"two-when\")\n" +
"            break\n" +
"        }\n" +
"        default:\n" +
"        {\n" +
"            print(\"default\")\n" +
"            break\n" +
"        }\n" +
"    }\n" +
"    var s = \"b\"\n" +
"    switch (s)\n" +
"    {\n" +
"        case \"a\":\n" +
"        {\n" +
"            print(\"A\")\n" +
"            break\n" +
"        }\n" +
"        case \"b\":\n" +
"        {\n" +
"            print(\"B\")\n" +
"            break\n" +
"        }\n" +
"        default:\n" +
"        {\n" +
"            print(\"Z\")\n" +
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
"    print(sum)\n" +
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
    print(s.Area())
    print(c.Clone())
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
    print(d.B())
    var e: IExtra = d
    print(e.X())
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
    print(Config.GetMax())
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
    print(Config.GetMax())
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
    print(Config.GetOrder())
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
    print(a.GetBase())
    print(Account.Seq)
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
