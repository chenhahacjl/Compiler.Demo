using System;
using System.Diagnostics;
using System.IO;
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
        {
            var syntaxTree = Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(source);
            var compilation = Cocoa.CodeAnalysis.Compilation.Create(new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, syntaxTree);
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

public class Circle: Shape
{
    private _radius: int

    public constructor(name: string, radius: int): base(name)
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

public class Circle: IShape
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

public interface IColoredShape: IShape
{
    function Color(): string
}

public class ColoredSquare: IColoredShape
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

public class Dog: IAnimal
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

public class Puppy: Dog
{
    public constructor(age: int): base(age)
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

public abstract class BaseUnit: IFighter
{
    public function Name(): string
    {
        return ""unit""
    }

    public abstract function Power(): int
}

public class Knight: BaseUnit
{
    public function Power(): int
    {
        return 10
    }
}

public class Archer: BaseUnit
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

public class Resource: IDisposable
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
        public void CStyleFor_PostfixIncrement_OnDotnetHost()
        {
            var (exitCode, stdout) = EmitAndRun(@"
function Main()
{
    var sum = 0
    for (var i = 0; i < 5; i++)
    {
        sum = sum + i
    }
    print(sum)
    var j = 10
    j--
    print(j)
    j++
    print(j)
    var total = 0
    for (;;)
    {
        total = total + 1
        if total == 3
        {
            break
        }
    }
    print(total)
    var k = 0
    for (; k < 4; k = k + 1)
    {
        if k == 2
        {
            continue
        }
        print(k)
    }
}", "e2e-cstyle-for");

            Assert.Equal(0, exitCode);
            Assert.Equal("10\r\n9\r\n10\r\n3\r\n0\r\n1\r\n3\r\n", stdout);
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

public class Dog: IDog
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
    }
}
