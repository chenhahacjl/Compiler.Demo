using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit.Native;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native
{
    /// <summary>
    /// 6e-M19 M4：native 对象模型 e2e（x64 + x86 双平台）——
    /// new/字段/构造链/this、继承 + virtual/override vtable 虚分派、base.Method() 直调、
    /// Object 成员面（ToString/GetHashCode/Equals/GetType 默认与 override）、System.Type
    /// （Name/FullName/ToString）、== / != 引用相等、静态字段零值存储、对象数组元素清零。
    /// 程序形状与 IlE2eTests 对应用例一致，锁定三后端语义一致。
    /// </summary>
    public class NativeOopE2eTests
    {
        private static string GetExePath(string name, TargetPlatform platform)
        {
            var directory = Path.Combine(Path.GetTempPath(), "cocoa-native-oop-tests");
            Directory.CreateDirectory(directory);
            var suffix = platform.Arch == Architecture.X86 ? "-x86" : "";
            return Path.Combine(directory, name + suffix + ".exe");
        }

        private static (int ExitCode, string Stdout) EmitNativeAndRun(string source, string name, TargetPlatform platform)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var exePath = GetExePath(name, platform);
            var diagnostics = compilation.EmitNative(name, exePath, platform);

            Assert.Empty(diagnostics);
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
            var stdout = Encoding.Unicode.GetString(output.ToArray());
            return (process.ExitCode, stdout);
        }

        public static IEnumerable<object[]> GetPlatforms()
        {
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X64) };
            yield return new object[] { new TargetPlatform(TargetOS.Windows, Architecture.X86) };
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Oop_Fields_Ctor_Property_New(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

public class Person
{
    private _name: string
    private _age: i32

    public constructor(name: string, age: i32)
    {
        _name = name
        _age = age
    }

    public property Age: i32 { get set }

    public function NextAge(): i32
    {
        return _age + 1
    }

    public function Greet(): string
    {
        return ""Hello, "" + _name
    }
}

function Main()
{
    var p = new Person(""Alice"", 30)
    Console.WriteLine(p.Greet())
    Console.WriteLine(p.NextAge())
    p.Age = 31
    Console.WriteLine(p.Age)
}", "oop-basic", (TargetPlatform)platform);

            Assert.Equal(0, exitCode);
            Assert.Equal("Hello, Alice\r\n31\r\n31\r\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Oop_Inheritance_VirtualDispatch_BaseCall_CtorChain(object platform)
        {
            // 与 IlE2eTests.Oop_Inheritance_Polymorphism_Static_Property_OnDotnetHost 同形
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

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

function Main()
{
    var s: Shape = new Circle(""big"", 4)
    Console.WriteLine(s.Describe())
    var c = new Circle(""small"", 2)
    Console.WriteLine(c.Area)
    Console.WriteLine(c.Describe())
}", "oop-poly", (TargetPlatform)platform);

            Assert.Equal(0, exitCode);
            Assert.Equal("big4\r\n4\r\nsmall2\r\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Oop_Override_ToString_BaseToString(object platform)
        {
            // 与 IlE2eTests.ObjectModel_Override_ToString_VirtualDispatch_OnDotnetHost 同形：
            // 经基类引用调用 → 虚分派到派生 override；base.ToString() 非虚直调不递归
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

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
    var a = new Animal()
    Console.WriteLine(a.ToString())
}", "oop-tostring", (TargetPlatform)platform);

            Assert.Equal(0, exitCode);
            Assert.Equal("dog(animal)\r\nD=dog(animal)\r\nanimal\r\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Oop_DefaultToString_GetHashCode_Equals_ReferenceEquality(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

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

    // 引用相等（== / != 与 ReferenceEquals 一致）
    Console.WriteLine(p == q)
    Console.WriteLine(p == r)
    Console.WriteLine(p != q)
    var d = new Point3D(2)
    var o: object = d
    Console.WriteLine(o == d)
    Console.WriteLine(o == p)

    // 静态相等：引用同/异；值类型装箱语义恒不等
    Console.WriteLine(Object.Equals(p, q))
    Console.WriteLine(Object.Equals(p, r))
    Console.WriteLine(Object.ReferenceEquals(1, 1))

    // 默认 ToString = 类型全名；GetHashCode 同对象稳定
    Console.WriteLine(p.ToString().Contains(""Point""))
    Console.WriteLine(p.ToString() == r.ToString())
    var h1 = p.GetHashCode()
    var h2 = p.GetHashCode()
    Console.WriteLine(h1 == h2)
}", "oop-equality", (TargetPlatform)platform);

            Assert.Equal(0, exitCode);
            Assert.Equal("True\r\nFalse\r\nFalse\r\nTrue\r\nFalse\r\nTrue\r\nFalse\r\nFalse\r\nTrue\r\nTrue\r\nTrue\r\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Oop_Override_GetHashCode_Equals(object platform)
        {
            // 与 IlE2eTests.ObjectModel_Override_GetHashCode_Equals_OnDotnetHost 同形
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

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
    if b.Equals(new Box(9)) return 4
    return 0
}", "oop-override-gh-eq", (TargetPlatform)platform);

            Assert.Equal(0, exitCode);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Oop_GetType_TypeName_FullName_Primitives(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

public class Animal
{
}

public class Dog extends Animal
{
    public override function ToString(): string
    {
        return ""dog""
    }
}

function Main()
{
    var a = new Dog()
    var t = a.GetType()
    Console.WriteLine(t.FullName)
    Console.WriteLine(t.Name)
    Console.WriteLine(t.ToString())

    // 具体类 Type 与声明基类 Type 不同名（vtable 即 Type 对象）
    var at: Animal = a
    Console.WriteLine(at.GetType().Name == ""Dog"")

    // 基元 GetType → 封装类全名
    Console.WriteLine(5.GetType().FullName)
    Console.WriteLine(""s"".GetType().Name)
    Console.WriteLine(true.GetType().FullName)
    Console.WriteLine('c'.GetType().Name)
    Console.WriteLine(1.5.GetType().FullName)
}", "oop-gettype", (TargetPlatform)platform);

            Assert.Equal(0, exitCode);
            Assert.Equal("Dog\r\nDog\r\nDog\r\nTrue\r\nSystem.Int32\r\nString\r\nSystem.Boolean\r\nChar\r\nSystem.Double\r\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Oop_StaticFields_ZeroInit_And_ObjectArrayElements(object platform)
        {
            // 注：类数组零值填充（EmitZeroFillElements）已发射；null 字面量随 M5 引入后
            // 补充"未填充元素可安全判空"的断言（当前语言无 null，仅锁定存取与计数）。
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

public class Counter
{
    public static Total: i32

    public constructor()
    {
        Total = Total + 1
    }
}

public class Node
{
    public Value: i32

    public constructor(v: i32)
    {
        Value = v
    }
}

function Main()
{
    // 静态字段零值默认（无初始化器）
    Console.WriteLine(Counter.Total)

    var c1 = new Counter()
    var c2 = new Counter()
    Console.WriteLine(Counter.Total)

    // 类数组元素存取
    var nodes: Node[] = new Node[3]
    Console.WriteLine(nodes.Length)
    nodes[0] = new Node(7)
    nodes[1] = new Node(8)
    nodes[2] = new Node(9)
    Console.WriteLine(nodes[0].Value + nodes[1].Value + nodes[2].Value)
}", "oop-static-array", (TargetPlatform)platform);

            Assert.Equal(0, exitCode);
            Assert.Equal("0\r\n2\r\n3\r\n24\r\n", stdout);
        }

        [Fact]
        public void Oop_Interface_StillRejected()
        {
            var syntaxTree = SyntaxTree.Parse(@"using System

public interface IShape
{
    function Area(): i32
}

function Main()
{
    var x: i32 = 0
    Console.WriteLine(x)
}");
            var compilation = Compilation.Create(syntaxTree);
            var diagnostics = compilation.EmitNative("oop-iface", Path.Combine(Path.GetTempPath(), "cocoa-native-oop-iface.exe"), new TargetPlatform(TargetOS.Windows, Architecture.X64));
            Assert.Contains(diagnostics, d => d.Message.Contains("interface 'IShape' 暂不支持 native 后端"));
        }

        [Fact]
        public void Oop_StaticInitializer_StillRejected()
        {
            var syntaxTree = SyntaxTree.Parse(@"using System

public class Config
{
    public static Version: i32 = 3
}

function Main()
{
    Console.WriteLine(Config.Version)
}");
            var compilation = Compilation.Create(syntaxTree);
            var diagnostics = compilation.EmitNative("oop-sinit", Path.Combine(Path.GetTempPath(), "cocoa-native-oop-sinit.exe"), new TargetPlatform(TargetOS.Windows, Architecture.X64));
            Assert.Contains(diagnostics, d => d.Message.Contains("静态构造函数或静态字段初始化器"));
        }

        /// <summary>6e-M19 M5-a：null 字面量——引用比较（Cmp 0）/ 赋值转换 / 三元 null 分支 / 空串拼接与打印。</summary>
        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Null_Literal_Comparisons_Ternary_Concat(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(@"using System

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
}", "m5a-null-native", (TargetPlatform)platform);

            Assert.Equal(0, exitCode);
            Assert.Equal("False\r\nTrue\r\nTrue\r\nFalse\r\nTrue\r\nTrue\r\nx\r\n\r\nFalse\r\n", stdout);
        }
    }
}
