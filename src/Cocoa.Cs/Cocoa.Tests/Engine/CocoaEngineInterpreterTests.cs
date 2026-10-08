using System;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.Engine;
using Xunit;

namespace Cocoa.Tests.Engine
{
    /// <summary>
    /// M1：CocoaEngine 嵌入式引擎核心——DoString 返回值/submission 链保持/全局变量读写/
    /// Output 事件捕获/编译错误不持久化/Reset。
    /// </summary>
    public class CocoaEngineInterpreterTests
    {
        private static object? Evaluate(string code)
        {
            using var engine = new CocoaEngine();
            var result = engine.DoString(code);
            Assert.False(result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
            return result.Value;
        }

        [Fact]
        public void DoString_ReturnsLastExpressionValue()
        {
            Assert.Equal(42, Evaluate("21 + 21"));
            Assert.Equal("test", Evaluate("\"test\""));
            Assert.Equal(true, Evaluate("3 < 4"));
        }

        [Fact]
        public void DoString_CompilationError_ReturnsDiagnosticsWithoutValue()
        {
            using var engine = new CocoaEngine();
            var result = engine.DoString("var x = ;");
            Assert.True(result.Diagnostics.HasErrors());
            Assert.Null(result.Value);
        }

        [Fact]
        public void SubmissionChain_KeepsFunctionsAcrossDoString()
        {
            using var engine = new CocoaEngine();
            var first = engine.DoString(@"function Add(a: i32, b: i32): i32 { return a + b }");
            Assert.True(first.Diagnostics.IsEmpty, string.Join("\n", first.Diagnostics.Select(d => d.Message)));
            // 后续提交可引用前一提交的函数（本地函数跨提交复用验证）
            var second = engine.DoString("var y = Add(1, 2) return y");
            Assert.True(second.Diagnostics.IsEmpty, string.Join("\n", second.Diagnostics.Select(d => d.Message)));
            Assert.Equal(3, second.Value);
        }

        [Fact]
        public void GlobalVariable_GetSetAcrossSubmissions()
        {
            using var engine = new CocoaEngine();
            var result = engine.DoString(@"var counter = 0
function Bump() { counter = counter + 1 }");
            Assert.True(result.Diagnostics.IsEmpty, string.Join("\n", result.Diagnostics.Select(d => d.Message)));

            Assert.Equal(0, engine.GetGlobal("counter"));
            engine.SetGlobal("counter", 41);
            Assert.Equal(41, engine.GetGlobal("counter"));

            // Co 侧函数可见 C# 侧写入
            var bump = engine.DoString("Bump()");
            Assert.False(bump.Diagnostics.HasErrors());
            Assert.Equal(42, engine.GetGlobal("counter"));
        }

        [Fact]
        public void GetGlobal_UnknownName_ReturnsNull()
        {
            using var engine = new CocoaEngine();
            Assert.Null(engine.GetGlobal("no_such_global"));
        }

        [Fact]
        public void SetGlobal_UnknownName_Throws()
        {
            using var engine = new CocoaEngine();
            var ex = Assert.Throws<ArgumentException>(() => engine.SetGlobal("no_such_global", 1));
            Assert.Contains("no_such_global", ex.Message);
        }

        [Fact]
        public void OutputEvent_CapturesWriteLine()
        {
            using var engine = new CocoaEngine();
            var outputs = new System.Collections.Generic.List<string>();
            engine.Output += (_, text) => outputs.Add(text);

            var result = engine.DoString(@"using System
Console.WriteLine(""hello engine"")
return 42");
            Assert.False(result.Diagnostics.HasErrors());
            Assert.Equal(42, result.Value);
            Assert.Contains(outputs, o => o.Contains("hello engine"));
        }

        [Fact]
        public void Reset_ClearsSubmissionsAndGlobals()
        {
            using var engine = new CocoaEngine();
            engine.DoString(@"var counter = 0");
            engine.SetGlobal("counter", 5);
            Assert.Equal(5, engine.GetGlobal("counter"));

            engine.Reset();

            // 全局字典清空（变量符号已随 submission 链清掉 → 未声明）
            Assert.Null(engine.GetGlobal("counter"));
            Assert.Throws<ArgumentException>(() => engine.SetGlobal("counter", 1));
        }

        [Fact]
        public void Dispose_ThenDoString_Throws()
        {
            var engine = new CocoaEngine();
            engine.Dispose();
            Assert.Throws<ObjectDisposedException>(() => engine.DoString("1"));
        }

        [Fact]
        public void Call_AddsTwoInts()
        {
            using var engine = new CocoaEngine();
            var declared = engine.DoString(@"function Add(a: i32, b: i32): i32 { return a + b }");
            Assert.True(declared.Diagnostics.IsEmpty, string.Join("\n", declared.Diagnostics.Select(d => d.Message)));

            var result = engine.Call("Add", 21, 21);
            Assert.Equal(42, result);
        }

        [Fact]
        public void Call_StringParameter()
        {
            using var engine = new CocoaEngine();
            engine.DoString(@"function Greet(name: string): string { return ""Hello "" + name }");

            var result = engine.Call("Greet", "Cocoa");
            Assert.Equal("Hello Cocoa", result);
        }

        [Fact]
        public void Call_Void_ReturnsNull()
        {
            using var engine = new CocoaEngine();
            engine.DoString(@"function Noop() { var x = 1 }");

            var result = engine.Call("Noop");
            Assert.Null(result);
        }

        [Fact]
        public void Call_ReadsGlobalVariable_AcrossSubmissions()
        {
            using var engine = new CocoaEngine();
            engine.DoString(@"var factor = 2
function Scale(x: i32): i32 { return x * factor }");

            Assert.Equal(10, engine.Call("Scale", 5));
            engine.SetGlobal("factor", 3);
            Assert.Equal(15, engine.Call("Scale", 5));
        }

        [Fact]
        public void Call_UnknownFunction_Throws()
        {
            using var engine = new CocoaEngine();
            engine.DoString(@"function Known() { }");

            var ex = Assert.Throws<ArgumentException>(() => engine.Call("Unknown"));
            Assert.Contains("Unknown", ex.Message);
        }

        [Fact]
        public void Call_BeforeAnySubmission_Throws()
        {
            using var engine = new CocoaEngine();
            var ex = Assert.Throws<ArgumentException>(() => engine.Call("F"));
            Assert.Contains("未找到可调用的顶层函数", ex.Message);
        }

        [Fact]
        public void Call_WrongArgumentCount_Throws()
        {
            using var engine = new CocoaEngine();
            engine.DoString(@"function Add(a: i32, b: i32): i32 { return a + b }");

            Assert.Throws<ArgumentException>(() => engine.Call("Add", 1));
        }

        [Fact]
        public void Call_ArgumentTypeMapping_ConvertsIntegral()
        {
            using var engine = new CocoaEngine();
            engine.DoString(@"function Twice(x: i32): i32 { return x * 2 }");

            // long → i32 编组转换
            var result = engine.Call("Twice", 10L);
            Assert.Equal(20, result);
        }
    }
}