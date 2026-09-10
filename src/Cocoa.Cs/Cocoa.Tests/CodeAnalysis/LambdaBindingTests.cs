using System.Collections.Generic;
using System.Text;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Evaluation;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// Lambda / 函数值绑定与求值测试（6e-M22 C4-a，Evaluator 后端）：提升、变量存储、间接调用、
    /// 方法组转换、函数类型参数传递。
    /// IL / native 发射于 C4-b/C4-c 接入（当前统一报门禁诊断）。
    /// </summary>
    public class LambdaBindingTests
    {
        private const string VariableInvokeProgram = @"using System

function Main(): i32
{
    var f: (i32) -> i32 = (x: i32) => x * 2
    Console.WriteLine(f(21))

    if f(5) != 10
    {
        return 1
    }

    return 0
}";

        private const string MethodGroupProgram = @"using System

function Double(x: i32): i32
{
    return x * 2
}

function Main(): i32
{
    var g: (i32) -> i32 = Double
    Console.WriteLine(g(8))

    if g(3) != 6
    {
        return 1
    }

    return 0
}";

        private const string HigherOrderProgram = @"using System

function Apply(f: (i32) -> i32, v: i32): i32
{
    return f(v)
}

function Inc(x: i32): i32
{
    return x + 1
}

function Main(): i32
{
    Console.WriteLine(Apply((x: i32) => x + 1, 41))
    Console.WriteLine(Apply(Inc, 9))

    if Apply((x: i32) => x * 3, 4) != 12
    {
        return 1
    }

    return 0
}";

        [Fact]
        public void Evaluator_LambdaVariable_Invoke()
        {
            var result = Evaluate(VariableInvokeProgram);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Evaluator_MethodGroup_ConvertsAndInvokes()
        {
            var result = Evaluate(MethodGroupProgram);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Evaluator_HigherOrder_FunctionTypeParameter()
        {
            var result = Evaluate(HigherOrderProgram);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Binder_Lambda_ImplicitParametersWithoutTarget_Diagnosed()
        {
            // .co 方言：隐式参数在解析层即拒绝（C# 方言允许，绑定层目标推导见 C4 后续）
            var tree = SyntaxTree.Parse("let f = (x: i64, y) => x");
            Assert.Contains(tree.Diagnostics, d => d.IsError && d.Message.Contains("须显式标注类型"));
        }

        [Fact]
        public void Binder_MethodGroup_OverloadAmbiguity_Diagnosed()
        {
            var code = @"using System

function Pick(x: i32): i32 { return x }
function Pick(x: string): string { return x }

function Main()
{
    var g: (i32) -> i32 = Pick
}";
            var result = Evaluate(code);
            Assert.Contains(result.Diagnostics, d => d.IsError && d.Message.Contains("重载"));
        }

        // ------------------------------------------------------------------
        // 闭包捕获（6e-M22 C5）：Evaluator 后端
        // ------------------------------------------------------------------

        private const string ClosureCounterProgram = @"using System

function MakeCounter(): () -> i32
{
    var count = 0

    return () =>
    {
        count = count + 1
        return count
    }
}

function Main(): i32
{
    var c1 = MakeCounter()
    var c2 = MakeCounter()

    Console.WriteLine(c1())
    Console.WriteLine(c1())
    Console.WriteLine(c2())
    Console.WriteLine(c1())

    if c1() != 4 || c2() != 2
    {
        return 1
    }

    return 0
}";

        private const string ClosureParameterProgram = @"using System

function Adder(n: i32): (i32) -> i32
{
    return (x: i32) =>
    {
        return x + n
    }
}

function Main(): i32
{
    var add5 = Adder(5)
    Console.WriteLine(add5(10))

    var add1 = Adder(1)

    if add1(7) != 8
    {
        return 1
    }

    return 0
}";

        [Fact]
        public void Evaluator_Closure_Counter_IndependentInstances()
        {
            var result = Evaluate(ClosureCounterProgram);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Evaluator_Closure_ParameterCapture()
        {
            var result = Evaluate(ClosureParameterProgram);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Theory]
        [MemberData(nameof(GetNativePlatforms))]
        public void Native_Closure_Counter_IndependentInstances(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(ClosureCounterProgram, "c5_counter_nat", (Cocoa.Targeting.TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal("1\n2\n1\n3\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetNativePlatforms))]
        public void Native_Closure_ParameterCapture(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(ClosureParameterProgram, "c5_param_nat", (Cocoa.Targeting.TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal("15\n", stdout);
        }

        // ------------------------------------------------------------------
        // native 后端（6e-M22 C4-c：[typeId][fnptr][env] 三字对象 + CallReg）
        // ------------------------------------------------------------------

        public static System.Collections.Generic.IEnumerable<object[]> GetNativePlatforms()
        {
            yield return new object[] { new Cocoa.Targeting.TargetPlatform(Cocoa.Targeting.TargetOS.Windows, Cocoa.Targeting.Architecture.X64) };
            yield return new object[] { new Cocoa.Targeting.TargetPlatform(Cocoa.Targeting.TargetOS.Windows, Cocoa.Targeting.Architecture.X86) };
        }

        [Theory]
        [MemberData(nameof(GetNativePlatforms))]
        public void Native_LambdaVariable_Invoke(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(VariableInvokeProgram, "c4c_lambda_nat", (Cocoa.Targeting.TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal("42\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetNativePlatforms))]
        public void Native_MethodGroup_ConvertsAndInvokes(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(MethodGroupProgram, "c4c_methodgroup_nat", (Cocoa.Targeting.TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal("16\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetNativePlatforms))]
        public void Native_HigherOrder_FunctionTypeParameter(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(HigherOrderProgram, "c4c_higher_nat", (Cocoa.Targeting.TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal("42\n10\n", stdout);
        }

        private static (int ExitCode, string Stdout) EmitNativeAndRun(string source, string name, Cocoa.Targeting.TargetPlatform platform)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var compilation = Compilation.Create(syntaxTree);
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cocoa_c4c_native_tests");
            System.IO.Directory.CreateDirectory(directory);
            var suffix = platform.Arch == Cocoa.Targeting.Architecture.X86 ? "-x86" : "";
            var exePath = System.IO.Path.Combine(directory, name + suffix + ".exe");
            var diagnostics = compilation.EmitNative(name, exePath, platform);

            Assert.True(diagnostics.IsEmpty, string.Join("; ", diagnostics.Select(d => d.Message)));

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
        // IL 后端（6e-M22 C4-b：Func`N 委托映射 + TypeSpec 父 + VAR 开放 Invoke 签名）
        // ------------------------------------------------------------------

        [Fact]
        public void Il_LambdaVariable_Invoke()
        {
            var (exitCode, stdout) = EmitIlAndRun(VariableInvokeProgram, "c4b_lambda_il");
            Assert.Equal(0, exitCode);
            Assert.Equal("42\n", stdout);
        }

        [Fact]
        public void Il_MethodGroup_ConvertsAndInvokes()
        {
            var (exitCode, stdout) = EmitIlAndRun(MethodGroupProgram, "c4b_methodgroup_il");
            Assert.Equal(0, exitCode);
            Assert.Equal("16\n", stdout);
        }

        [Fact]
        public void Il_HigherOrder_FunctionTypeParameter()
        {
            var (exitCode, stdout) = EmitIlAndRun(HigherOrderProgram, "c4b_higher_il");
            Assert.Equal(0, exitCode);
            Assert.Equal("42\n10\n", stdout);
        }

        // ------------------------------------------------------------------
        // delegate 声明 + 使用（6e-M22 D-B）
        // ------------------------------------------------------------------

        private const string DelegateDeclareProgram = @"using System

delegate void PrintHandler(msg: string)

function Main(): i32
{
    var h: PrintHandler = (m: string) =>
    {
        Console.WriteLine(m)
    }
    Console.WriteLine(""created"")
    h(""hello"")
    return 0
}";

        [Fact]
        public void Evaluator_DelegateVariable_AssignAndInvoke()
        {
            var result = Evaluate(DelegateDeclareProgram);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Evaluator_Delegate_TypeResolves()
        {
            var code = @"using System
delegate void Handler(a: Object)
function Test(d: Handler): void { }
function Main(): i32 { return 0 }";
            var result = Evaluate(code);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
        }

        private const string DelegateMethodGroupProgram = @"using System

delegate IntTransform(x: i32): i32

function Double(x: i32): i32
{
    return x * 2
}

function Main(): i32
{
    var t: IntTransform = Double
    Console.WriteLine(t(8))

    if t(3) != 6
    {
        return 1
    }

    return 0
}";

        // ── C5+ 多播事件（升级自单播 v1：+= 尾插 / -= 引用相等移除首匹配 / 触发快照遍历）──

        private const string EventBasicProgram = @"using System

class Greeter
{
    public event onGreet: (string) -> void

    public function Fire(msg: string): void
    {
        Console.WriteLine(""firing"")
        onGreet(msg)
    }
}

function Main(): i32
{
    var g = new Greeter()
    g.onGreet += (m: string) =>
    {
        Console.WriteLine(m)
    }
    Console.WriteLine(""subscribed"")
    g.Fire(""hello"")
    return 0
}";

        [Fact]
        public void Evaluator_Event_BasicSubscription()
        {
            var result = Evaluate(EventBasicProgram);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Il_Event_BasicSubscription()
        {
            var (exitCode, stdout) = EmitIlAndRun(EventBasicProgram, "c5e_event_il");
            Assert.Equal(0, exitCode);
            Assert.Equal("subscribed\nfiring\nhello\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetNativePlatforms))]
        public void Native_Event_BasicSubscription(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(EventBasicProgram, "c5e_event_nat", (Cocoa.Targeting.TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal("subscribed\nfiring\nhello\n", stdout);
        }

        private const string EventMulticastProgram = @"using System

class Greeter
{
    public event onGreet: (string) -> void

    public function Fire(msg: string): void
    {
        onGreet(msg)
    }
}

function HandleA(msg: string): void
{
    Console.WriteLine(""A:"" + msg)
}

function HandleB(msg: string): void
{
    Console.WriteLine(""B:"" + msg)
}

function Main(): i32
{
    var g = new Greeter()
    var hA: (string) -> void = HandleA
    g.onGreet += hA
    g.onGreet += HandleB
    g.Fire(""x"")

    g.onGreet -= hA
    g.Fire(""y"")

    return 0
}";

        private const string ExpectedMulticastOutput = "A:x\nB:x\nB:y\n";

        [Fact]
        public void Evaluator_Event_Multicast_SubscribeOrderAndRemove()
        {
            var (result, output) = EvaluateWithOutput(EventMulticastProgram);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
            Assert.Equal(ExpectedMulticastOutput, output);
        }

        [Fact]
        public void Il_Event_Multicast_SubscribeOrderAndRemove()
        {
            var (exitCode, stdout) = EmitIlAndRun(EventMulticastProgram, "c5e_multi_il");
            Assert.Equal(0, exitCode);
            Assert.Equal(ExpectedMulticastOutput, stdout);
        }

        [Theory]
        [MemberData(nameof(GetNativePlatforms))]
        public void Native_Event_Multicast_SubscribeOrderAndRemove(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(EventMulticastProgram, "c5e_multi_nat", (Cocoa.Targeting.TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal(ExpectedMulticastOutput, stdout);
        }

        private const string EventEdgeProgram = @"using System

class Bell
{
    public event Ring: () -> void

    public function Chime(): void
    {
        Console.WriteLine(""ring:"")
        Ring()
    }
}

function Loud(): void
{
    Console.WriteLine(""boom"")
}

function Main(): i32
{
    var b = new Bell()

    // 空事件触发 no-op
    b.Chime()

    var h: () -> void = Loud

    // 重复订阅同一引用 + 单次移除仅去首个匹配
    b.Ring += h
    b.Ring += h
    b.Chime()
    b.Ring -= h
    b.Chime()

    // 清空后重订阅（清空回 null）
    b.Ring -= h
    b.Chime()
    b.Ring += h
    b.Chime()

    return 0
}";

        private const string ExpectedEdgeOutput =
            "ring:\n" +
            "ring:\nboom\nboom\n" +
            "ring:\nboom\n" +
            "ring:\n" +
            "ring:\nboom\n";

        [Fact]
        public void Evaluator_Event_EdgeCases_NoopDuplicateResubscribe()
        {
            var (result, output) = EvaluateWithOutput(EventEdgeProgram);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
            Assert.Equal(ExpectedEdgeOutput, output);
        }

        [Fact]
        public void Il_Event_EdgeCases_NoopDuplicateResubscribe()
        {
            var (exitCode, stdout) = EmitIlAndRun(EventEdgeProgram, "c5e_edge_il");
            Assert.Equal(0, exitCode);
            Assert.Equal(ExpectedEdgeOutput, stdout);
        }

        [Theory]
        [MemberData(nameof(GetNativePlatforms))]
        public void Native_Event_EdgeCases_NoopDuplicateResubscribe(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(EventEdgeProgram, "c5e_edge_nat", (Cocoa.Targeting.TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal(ExpectedEdgeOutput, stdout);
        }

        [Fact]
        public void Binder_Event_ExternalRead_Diagnosed()
        {
            var code = @"using System

class Box
{
    public event Pop: () -> void
}

function Main(): i32
{
    var b = new Box()
    var h = b.Pop
    return 0
}";
            var result = Evaluate(code);
            Assert.Contains(result.Diagnostics, d => d.IsError && d.Message.Contains("不能作为值"));
        }

        [Fact]
        public void Binder_Event_ExternalCall_Diagnosed()
        {
            var code = @"using System

class Box
{
    public event Pop: () -> void
}

function Main(): i32
{
    var b = new Box()
    b.Pop()
    return 0
}";
            var result = Evaluate(code);
            Assert.Contains(result.Diagnostics, d => d.IsError && d.Message.Contains("不能作为值"));
        }

        [Fact]
        public void Binder_Event_StaticEvent_Diagnosed()
        {
            var code = @"using System

class Box
{
    public static event Pop: () -> void
}

function Main(): i32
{
    return 0
}";
            var result = Evaluate(code);
            Assert.Contains(result.Diagnostics, d => d.IsError && d.Message.Contains("静态事件"));
        }

        [Fact]
        public void Binder_Event_DirectAssignOutsideClass_Diagnosed()
        {
            var code = @"using System

class Box
{
    public event Pop: () -> void
}

function Main(): i32
{
    var b = new Box()
    b.Pop = () => { }
    return 0
}";
            var result = Evaluate(code);
            Assert.Contains(result.Diagnostics, d => d.IsError);
        }

        private const string DelegateBackedEventProgram = @"using System

delegate Handler(msg: string): void

class Notifier
{
    public event onMsg: Handler

    public function Send(msg: string): void
    {
        onMsg(msg)
    }
}

function PrintIt(msg: string): void
{
    Console.WriteLine(""got:"" + msg)
}

function Main(): i32
{
    var n = new Notifier()
    var h: Handler = PrintIt
    n.onMsg += h
    n.Send(""ping"")
    return 0
}";

        [Fact]
        public void Evaluator_Event_DelegateTyped_RoundTrip()
        {
            var (result, output) = EvaluateWithOutput(DelegateBackedEventProgram);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
            Assert.Equal("got:ping\n", output);
        }

        /// <summary>求值并捕获 Console 输出（Evaluator 走 Console.WriteLine）。</summary>
        private static (EvaluationResult Result, string Output) EvaluateWithOutput(string code)
        {
            var original = System.Console.Out;
            try
            {
                using var writer = new System.IO.StringWriter();
                System.Console.SetOut(writer);
                var result = Evaluate(code);
                return (result, writer.ToString().Replace("\r\n", "\n"));
            }
            finally
            {
                System.Console.SetOut(original);
            }
        }

        [Fact]
        public void Evaluator_Delegate_MethodGroup_AssignAndInvoke()
        {
            var result = Evaluate(DelegateMethodGroupProgram);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Theory]
        [MemberData(nameof(GetNativePlatforms))]
        public void Native_DeclareDelegate_AssignInvoke(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(DelegateDeclareProgram, "c5d_declare_nat", (Cocoa.Targeting.TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal("created\nhello\n", stdout);
        }

        [Theory]
        [MemberData(nameof(GetNativePlatforms))]
        public void Native_Delegate_MethodGroup_AssignAndInvoke(object platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(DelegateMethodGroupProgram, "c5d_mg_nat", (Cocoa.Targeting.TargetPlatform)platform);
            Assert.Equal(0, exitCode);
            Assert.Equal("16\n", stdout);
        }

        [Fact]
        public void Il_DelegateVariable_AssignAndInvoke()
        {
            var (exitCode, stdout) = EmitIlAndRun(DelegateDeclareProgram, "c5d_lambda_il");
            Assert.Equal(0, exitCode);
            Assert.Equal("created\nhello\n", stdout);
        }

        [Fact]
        public void Il_Delegate_MethodGroup_AssignAndInvoke()
        {
            var (exitCode, stdout) = EmitIlAndRun(DelegateMethodGroupProgram, "c5d_mg_il");
            Assert.Equal(0, exitCode);
            Assert.Equal("16\n", stdout);
        }

        private static (int ExitCode, string Stdout) EmitIlAndRun(string source, string name)
        {
            var syntaxTree = SyntaxTree.Parse(source);
            var references = new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };
            var compilation = Compilation.Create("Main", references, syntaxTree);
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cocoa_c4b_il_tests");
            System.IO.Directory.CreateDirectory(directory);
            var exePath = System.IO.Path.Combine(directory, name + ".exe");
            var diagnostics = compilation.Emit(name, references, exePath, Cocoa.Targeting.IlTarget.Parse("net9.0"));

            Assert.True(diagnostics.IsEmpty, string.Join("; ", diagnostics.Select(d => d.Message)));

            var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{exePath}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);

            return (process.ExitCode, stdout.Replace("\r\n", "\n"));
        }

        // ── C8: stdlib Collections（源码方式集成，.coa 序列化待 G7）──

        private const string CollectionsProgram = @"using System

namespace System.Collections.Generic
{
    public class List<T>
    {
        private field _items: T[]
        private field _count: i32

        public constructor()
        {
            _items = new T[4]
            _count = 0
        }

        public function Add(item: T): void
        {
            if _count >= _items.Length
            {
                var old = _items
                var grown = new T[old.Length * 2]
                var i = 0
                while i < old.Length
                {
                    grown[i] = old[i]
                    i = i + 1
                }
                _items = grown
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
    }
}

function Main(): i32
{
    var list = new List<i32>()
    list.Add(10)
    list.Add(20)
    list.Add(30)
    Console.WriteLine(list.Get(0))
    Console.WriteLine(list.Count())

    if list.Get(1) != 20 || list.Count() != 3
    {
        return 1
    }

    var strs = new List<string>()
    strs.Add(""hello"")
    Console.WriteLine(strs.Get(0))

    if strs.Count() != 1
    {
        return 1
    }

    return 0
}";

        [Fact]
        public void Evaluator_Collections_List_BasicOperations()
        {
            var result = Evaluate(CollectionsProgram);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        private static EvaluationResult Evaluate(string code)
        {
            var tree = SyntaxTree.Parse(code);
            var compilation = Compilation.Create(tree);
            return compilation.Evaluate(new Dictionary<VariableSymbol, object>());
        }

        // ── 6e-M22 委托真实类型化 M0：泛型 delegate + in/out 型变安全位诊断 ──

        [Fact]
        public void Binder_DelegateGeneric_VarianceAnnotations_InvokeSignatureUsesTypeParameters()
        {
            var code = @"using System
delegate Transform<in T, out U>(x: T): U
function Main(): i32 { return 0 }";
            var result = Evaluate(code);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
        }

        [Fact]
        public void Binder_DelegateGeneric_InVariantInReturn_PositionError()
        {
            // 对齐 C# CS1962：`in T` 不得出现在协变（返回）位置
            var code = @"using System
delegate Transform<in T>(x: T): T
function Main(): i32 { return 0 }";
            var result = Evaluate(code);
            Assert.Contains(result.Diagnostics, d => d.Message.Contains("in 逆变类型参数") && d.Message.Contains("CS1962"));
        }

        [Fact]
        public void Binder_DelegateGeneric_OutVariantInParameter_PositionError()
        {
            // 对齐 C# CS1961：`out T` 不得出现在逆变（参数）位置
            var code = @"using System
delegate Transform<out T>(x: T): T
function Main(): i32 { return 0 }";
            var result = Evaluate(code);
            Assert.Contains(result.Diagnostics, d => d.Message.Contains("out 协变类型参数") && d.Message.Contains("CS1961"));
        }

        [Fact]
        public void Binder_LambdaMethodTypeParameter_Variance_Annotation_Diagnosed()
        {
            // 类/方法类型参数不支持型变（仅 delegate/接口）
            var code = @"using System
class Box<in T>
{
}
function Main(): i32 { return 0 }";
            var result = Evaluate(code);
            Assert.Contains(result.Diagnostics, d => d.Message.Contains("型变注解"));
        }

        // ── 6e-M22 委托真实类型化 M1：方差赋值兼容（参数逆变 + 返回协变）──

        [Fact]
        public void Binder_DelegateVariance_ParameterContravariance_Assignable()
        {
            // Handler<in T, out R>：方法组 (Animal)->Animal 赋给 Handler<Dog, Animal>（目标参数 Dog 可赋给来源 Animal）
            var code = @"using System
class Animal
{
}
class Dog extends Animal
{
}
delegate Mapper<in T, out R>(x: T): R
function Label(a: Animal): Animal { return a }
function Main(): i32
{
    var h: Mapper<Dog, Animal> = Label
    return 0
}";
            var result = Evaluate(code);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
        }

        [Fact]
        public void Binder_DelegateVariance_ReturnCovariance_Assignable()
        {
            // 方法组 (Dog)->Dog 赋给 Handler<Dog, Animal>（返回 Dog→Animal 协变 + 参数恒等）
            var code = @"using System
class Animal
{
}
class Dog extends Animal
{
}
delegate Mapper<in T, out R>(x: T): R
function Pet(d: Dog): Dog { return d }
function Main(): i32
{
    var h: Mapper<Dog, Animal> = Pet
    return 0
}";
            var result = Evaluate(code);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
        }

        [Fact]
        public void Binder_DelegateVariance_ReturnNotAssignable_Diagnosed()
        {
            // 方法组 (Animal)->Animal 赋给 Handler<Animal, Dog>：返回 Animal→Dog 非引用上转 → 报错
            var code = @"using System
class Animal
{
}
class Dog extends Animal
{
}
delegate Mapper<in T, out R>(x: T): R
function Label(a: Animal): Animal { return a }
function Main(): i32
{
    var h: Mapper<Animal, Dog> = Label
    return 0
}";
            var result = Evaluate(code);
            Assert.Contains(result.Diagnostics, d => d.IsError && (d.Message.Contains("Cannot convert") || d.Message.Contains("不能")));
        }

        [Fact]
        public void Binder_DelegateVariance_GenericInstantiation_TypeKindIsDelegate()
        {
            // Handler<int, string> 实例化后 TypeKind 须为 Delegate（方可作事件处理器/委托变量）
            var code = @"using System
delegate DHandler<in T, out R>(x: T): R
function Main(): i32
{
    var h: DHandler<i32, string> = HandlerOfInt
    return 0
}
function HandlerOfInt(x: i32): string { return """" }";
            var result = Evaluate(code);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
        }

        // ── 6e-M22 委托真实类型化 M3：多播对象（+ 组合 / - 调用列表移除 / == 调用列表相等）──

        private const string DelegateMulticastProgram = @"using System
delegate IntTransform(x: i32): i32
function Double(x: i32): i32 { return x * 2 }
function Triple(x: i32): i32 { return x * 3 }
function Main(): i32
{
    var d: IntTransform = Double
    var e: IntTransform = Triple
    var m = d + e
    if m(2) != 6
    {
        return 1
    }

    if !((d + e) == (d + e))
    {
        return 2
    }

    var r = m - d
    if r(2) != 6
    {
        return 3
    }

    return 0
}";

        [Fact]
        public void Evaluator_Delegate_Multicast_CombineRemoveEquality()
        {
            var result = Evaluate(DelegateMulticastProgram);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Il_Delegate_Multicast_CombineRemoveEquality()
        {
            var (exitCode, stdout) = EmitIlAndRun(DelegateMulticastProgram, "c5d_multicast_il");
            Assert.Equal(0, exitCode);
        }

        // ── 6e-M22 委托真实类型化 M4：delegate 后备事件（C# 式 add/remove + Invoke 触发）──

        private const string DelegateBackedEventCSharpStyleProgram = @"using System
delegate GreetHandler(msg: string): void
class Greeter
{
    public event onGreet: GreetHandler
    public function Fire(msg: string): void
    {
        onGreet(msg)
    }
}
function PrintA(m: string): void { Console.WriteLine(""A:"" + m) }
function PrintB(m: string): void { Console.WriteLine(""B:"" + m) }
function Main(): i32
{
    var g = new Greeter()
    g.onGreet += PrintA
    g.onGreet += PrintB
    g.Fire(""hello"")
    g.onGreet -= PrintA
    g.Fire(""world"")
    return 0
}";

        [Fact]
        public void Evaluator_Event_DelegateBacked_SubscribeRaiseUnsubscribe()
        {
            var result = Evaluate(DelegateBackedEventCSharpStyleProgram);
            Assert.Empty(result.Diagnostics.Where(d => d.IsError));
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public void Il_Event_DelegateBacked_SubscribeRaiseUnsubscribe()
        {
            var (exitCode, stdout) = EmitIlAndRun(DelegateBackedEventCSharpStyleProgram, "c5e_delegate_il");
            Assert.Equal(0, exitCode);
            Assert.Equal("A:hello\nB:hello\nB:world\n", stdout);
        }

        // ── 6e-M22 委托真实类型化 M5：native 委托对象（构造/多播调用/组合/移除/相等/事件）──

        [Theory]
        [MemberData(nameof(GetNativePlatforms))]
        public void Native_Delegate_Multicast_CombineRemoveEquality(Cocoa.Targeting.TargetPlatform platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(DelegateMulticastProgram, "m5_multicast_nat", platform);
            Assert.Equal(0, exitCode);
        }

        [Theory]
        [MemberData(nameof(GetNativePlatforms))]
        public void Native_Event_DelegateBacked_SubscribeRaiseUnsubscribe(Cocoa.Targeting.TargetPlatform platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(DelegateBackedEventCSharpStyleProgram, "m5_event_nat", platform);
            Assert.Equal(0, exitCode);
            Assert.Equal("A:hello\nB:hello\nB:world\n", stdout);
        }

        private const string DelegateGenericProgram = @"using System
delegate Transform<T, R>(x: T): R
function Inc(x: i32): i32 { return x + 1 }
function Main(): i32
{
    var f: Transform<i32, i32> = Inc
    var g: Transform<i32, i32> = Inc
    var m = f + g
    if m(10) != 11 { return 1 }
    return 0
}";

        [Theory]
        [MemberData(nameof(GetNativePlatforms))]
        public void Native_Delegate_Generic_Monomorphized_CombineInvoke(Cocoa.Targeting.TargetPlatform platform)
        {
            var (exitCode, stdout) = EmitNativeAndRun(DelegateGenericProgram, "m5_generic_nat", platform);
            Assert.Equal(0, exitCode);
        }

        [Fact]
        public void Il_Delegate_Generic_Monomorphized_Invoke()
        {
            var (exitCode, stdout) = EmitIlAndRun(DelegateGenericProgram, "m5_generic_il");
            Assert.Equal(0, exitCode);
        }
    }
}
