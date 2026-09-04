using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Cocoa.CodeAnalysis;
using Cocoa.Targeting;
using Cocoa.CodeGen.Native;
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

        // 6e-M22 C1：实例泛型方法（非泛型类成员模板）
        private const string MemberGenericProgram = @"using System

public class Picker
{
    public function Pick<T>(a: T, b: T): T
    {
        return a
    }
}

function Main(): i32
{
    var picker = new Picker()
    Console.WriteLine(picker.Pick<i32>(11, 22))
    Console.WriteLine(picker.Pick<string>(""win"", ""lose""))

    if picker.Pick<i32>(5, 6) != 5
    {
        return 1
    }

    return 0
}";

        // 6e-M22 C1：类静态泛型方法（点号访问）
        private const string StaticGenericMethodProgram = @"using System

public class Codec
{
    public static function Second<T>(a: T, b: T): T
    {
        return b
    }
}

function Main(): i32
{
    Console.WriteLine(Codec.Second<i32>(1, 2))
    Console.WriteLine(Codec.Second<string>(""x"", ""y""))

    if Codec.Second<i32>(9, 8) != 8
    {
        return 1
    }

    return 0
}";

        // 6e-M22 C1：命名空间限定泛型函数
        private const string NamespaceGenericFunctionProgram = @"using System

namespace MyUtil
{
    function FirstOf<T>(a: T, b: T): T
    {
        return a
    }
}

function Main(): i32
{
    Console.WriteLine(MyUtil.FirstOf<i32>(3, 4))
    Console.WriteLine(MyUtil.FirstOf<string>(""m"", ""n""))

    if MyUtil.FirstOf<i32>(7, 6) != 7
    {
        return 1
    }

    return 0
}";

        // 6e-M22 C1：泛型类成员泛型方法（类实例化携带方法级类型参数模板 + 二次实例化）
        private const string GenericClassMemberTemplateProgram = @"using System

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

    public function Echo<U>(value: U): U
    {
        return value
    }
}

function Main(): i32
{
    var box = new Box<string>(""payload"")
    Console.WriteLine(box.Get())
    Console.WriteLine(box.Echo<i32>(77))

    if box.Echo<i32>(42) != 42
    {
        return 1
    }

    return 0
}";

        private const string ForeachProgram = @"using System
namespace System.Collections.Generic
{

public interface IEnumerable<T>
{
    function GetEnumerator(): IEnumerator<T>
}

public interface IEnumerator<T>
{
    function MoveNext(): bool
    property Current: T { get }
}

public class List<T> extends IEnumerable<T>
{
    private _items: T[]
    private _count: i32

    public constructor()
    {
        _items = new T[4]
        _count = 0
    }

    public function Add(item: T)
    {
        if _count == _items.Length
        {
            var bigger = new T[_count * 2]
            var i = 0
            while i < _count
            {
                bigger[i] = _items[i]
                i = i + 1
            }
            _items = bigger
        }
        _items[_count] = item
        _count = _count + 1
    }

    public function Get(index: i32): T
    {
        return _items[index]
    }

    public function Count(): i32
    {
        return _count
    }

    public function GetEnumerator(): ListEnumerator<T>
    {
        return new ListEnumerator<T>(this)
    }
}

public class ListEnumerator<T> extends IEnumerator<T>
{
    private _list: List<T>
    private _index: i32

    public constructor(list: List<T>)
    {
        _list = list
        _index = -1
    }

    public function MoveNext(): bool
    {
        _index = _index + 1
        return _index < _list.Count()
    }

    public property Current: T
    {
        get
        {
            return _list.Get(_index)
        }
    }
}

}

function Main(): i32
{
    var list = new List<i32>()
    list.Add(10)
    list.Add(20)
    list.Add(30)

    var sum = 0
    foreach (var x in list)
    {
        sum = sum + x
    }
    Console.WriteLine(sum)

    var names = new List<string>()
    names.Add(""a"")
    names.Add(""b"")
    foreach (var n in names)
    {
        Console.WriteLine(n)
    }

    if sum != 60
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
        public void Evaluator_ForeachEnumerator_SumsList()
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(ForeachProgram));
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Evaluator_MemberGenericMethod_ReturnsZero()
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(MemberGenericProgram));
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Evaluator_StaticGenericMethod_ReturnsZero()
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(StaticGenericMethodProgram));
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Evaluator_NamespaceGenericFunction_ReturnsZero()
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(NamespaceGenericFunctionProgram));
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Evaluator_GenericClassMemberTemplate_ReturnsZero()
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(GenericClassMemberTemplateProgram));
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        // 6e-M22 C1 负例：成员泛型方法元数不匹配
        [Fact]
        public void Evaluator_MemberGenericMethod_WrongArity_Diagnosed()
        {
            var code = @"using System

public class Picker
{
    public function Pick<T>(a: T, b: T): T
    {
        return a
    }
}

function Main()
{
    var picker = new Picker()
    Console.WriteLine(picker.Pick<i32, string>(1, 2))
}";
            var compilation = Compilation.Create(SyntaxTree.Parse(code));
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Contains(result.Diagnostics, d => d.IsError && d.Message.Contains("2 个类型实参"));
        }

        // 6e-M22 C1 负例：成员泛型方法约束违约
        [Fact]
        public void Evaluator_MemberGenericMethod_ConstraintViolation_Diagnosed()
        {
            var code = @"using System

public class Holder
{
    public function Grab<T>(value: T): T where T: class
    {
        return value
    }
}

function Main()
{
    var holder = new Holder()
    Console.WriteLine(holder.Grab<i32>(5))
}";
            var compilation = Compilation.Create(SyntaxTree.Parse(code));
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Contains(result.Diagnostics, d => d.IsError && d.Message.Contains("where T: class"));
        }

        // 6e-M22 C1：where T: struct 正例（值类型实参通过；运算符不作用于开放 T，仅按 T 值传递）
        private const string StructConstraintProgram = @"using System

public function Head<T>(values: T[]): T where T: struct
{
    return values[0]
}

function Main(): i32
{
    var numbers = new i32[] { 10, 20, 12 }
    Console.WriteLine(Head<i32>(numbers))

    var wides = new i64[] { 10, 30 }
    if Head<i64>(wides) != 10
    {
        return 1
    }

    return 0
}";

        [Fact]
        public void Evaluator_StructConstraint_AcceptsPrimitives()
        {
            var compilation = Compilation.Create(SyntaxTree.Parse(StructConstraintProgram));
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Il_StructConstraint_AcceptsPrimitives()
        {
            var (exitCode, stdout) = EmitIlAndRun(StructConstraintProgram, "generic_struct_il");
            Assert.Equal(0, exitCode);
            Assert.Equal("10\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Native_StructConstraint_AcceptsPrimitives(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(StructConstraintProgram, "generic_struct_native", (TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal("10\n", stdout);
        }

        // 6e-M22 C1 负例：struct 约束违约（string 是引用类型）
        [Fact]
        public void Evaluator_StructConstraint_RejectsReferenceType()
        {
            var code = @"using System

public class Values
{
    public static function Head<T>(values: T[]): T where T: struct
    {
        return values[0]
    }
}

function Main()
{
    Console.WriteLine(Values.Head<string>(new string[] { ""nope"" }))
}";
            var compilation = Compilation.Create(SyntaxTree.Parse(code));
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Contains(result.Diagnostics, d => d.IsError && d.Message.Contains("where T: struct"));
        }

        // 6e-M22 C1 负例：class 与 struct 约束互斥
        [Fact]
        public void Evaluator_StructAndClassConstraints_Conflict_Diagnosed()
        {
            var code = @"using System

public function Only<T>(value: T): T where T: struct where T: class
{
    return value
}

function Main()
{
    Console.WriteLine(Only<i32>(1))
}";
            var compilation = Compilation.Create(SyntaxTree.Parse(code));
            var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
            Assert.Contains(result.Diagnostics, d => d.IsError && d.Message.Contains("'struct' 与 'class' 约束"));
        }

        [Fact]
        public void Il_ForeachEnumerator_WritesValues()
        {
            var (exitCode, stdout) = EmitIlAndRun(ForeachProgram, "generic_foreach_il");
            Assert.Equal(0, exitCode);
            Assert.Equal("60\na\nb\n", stdout);
        }

        [Fact]
        public void Il_MemberGenericMethod_WritesValues()
        {
            var (exitCode, stdout) = EmitIlAndRun(MemberGenericProgram, "generic_member_il");
            Assert.Equal(0, exitCode);
            Assert.Equal("11\nwin\n", stdout);
        }

        [Fact]
        public void Il_StaticGenericMethod_WritesValues()
        {
            var (exitCode, stdout) = EmitIlAndRun(StaticGenericMethodProgram, "generic_static_il");
            Assert.Equal(0, exitCode);
            Assert.Equal("2\ny\n", stdout);
        }

        [Fact]
        public void Il_NamespaceGenericFunction_WritesValues()
        {
            var (exitCode, stdout) = EmitIlAndRun(NamespaceGenericFunctionProgram, "generic_ns_il");
            Assert.Equal(0, exitCode);
            Assert.Equal("3\nm\n", stdout);
        }

        [Fact]
        public void Il_GenericClassMemberTemplate_WritesValues()
        {
            var (exitCode, stdout) = EmitIlAndRun(GenericClassMemberTemplateProgram, "generic_cls_member_il");
            Assert.Equal(0, exitCode);
            Assert.Equal("payload\n77\n", stdout);
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

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Native_ForeachEnumerator_WritesValues(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(ForeachProgram, "generic_foreach_native", (TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal("60\na\nb\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Native_MemberGenericMethod_WritesValues(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(MemberGenericProgram, "generic_member_native", (TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal("11\nwin\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Native_StaticGenericMethod_WritesValues(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(StaticGenericMethodProgram, "generic_static_native", (TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal("2\ny\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Native_NamespaceGenericFunction_WritesValues(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(NamespaceGenericFunctionProgram, "generic_ns_native", (TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal("3\nm\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetPlatforms))]
        public void Native_GenericClassMemberTemplate_WritesValues(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(GenericClassMemberTemplateProgram, "generic_cls_member_native", (TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal("payload\n77\n", stdout);
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
            using var output = new MemoryStream();
            using var process = Process.Start(psi)!;
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
