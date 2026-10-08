using System;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.Engine;
using Xunit;

namespace Cocoa.Tests.Engine
{
    /// <summary>
    /// M4：CocoaEngine IlEmit 后端——DoString（发射+反射执行，Value 经 $eval）、
    /// Call（反射调顶层函数）、与 Interpreter 双后端一致性、IlEmit 已知限制
    /// （全局变量/Output 不支持）。
    /// </summary>
    public class CocoaEngineIlEmitTests
    {
        [Fact]
        public void IlEmit_DoString_ReturnsLastExpressionValue()
        {
            using var engine = new CocoaEngine(EngineBackend.IlEmit);
            var result = engine.DoString("21 + 21");
            Assert.False(result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
            Assert.Equal(42, result.Value);
        }

        [Fact]
        public void IlEmit_DoString_StringValue()
        {
            using var engine = new CocoaEngine(EngineBackend.IlEmit);
            var result = engine.DoString("\"hello\"");
            Assert.False(result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
            Assert.Equal("hello", result.Value);
        }

        [Fact]
        public void IlEmit_DoString_CompilationError_ReturnsDiagnostics()
        {
            using var engine = new CocoaEngine(EngineBackend.IlEmit);
            var result = engine.DoString("var x = ;");
            Assert.True(result.Diagnostics.HasErrors());
        }

        [Fact]
        public void IlEmit_Call_TopLevelFunction()
        {
            using var engine = new CocoaEngine(EngineBackend.IlEmit);
            var declared = engine.DoString(@"function Add(a: i32, b: i32): i32 { return a + b }");
            Assert.False(declared.Diagnostics.HasErrors(), string.Join("\n", declared.Diagnostics.Select(d => d.Message)));

            var result = engine.Call("Add", 21, 21);
            Assert.Equal(42, result);
        }

        [Fact]
        public void IlEmit_Call_AfterSubmissionChain()
        {
            using var engine = new CocoaEngine(EngineBackend.IlEmit);
            var declared = engine.DoString(@"function Twice(x: i32): i32 { return x * 2 }");
            Assert.False(declared.Diagnostics.HasErrors(), string.Join("\n", declared.Diagnostics.Select(d => d.Message)));

            Assert.Equal(10, engine.Call("Twice", 5));
        }

        [Fact]
        public void IlEmit_Call_UnknownFunction_Throws()
        {
            using var engine = new CocoaEngine(EngineBackend.IlEmit);
            engine.DoString(@"function Known() { }");
            var ex = Assert.Throws<ArgumentException>(() => engine.Call("Unknown"));
            Assert.Contains("Unknown", ex.Message);
        }

        [Fact]
        public void IlEmit_GlobalVariables_ReflectionReadWrite()
        {
            using var engine = new CocoaEngine(EngineBackend.IlEmit);
            var declared = engine.DoString(@"var counter = 0
function Scale(x: i32): i32 { counter = counter + x; return counter }");
            Assert.True(declared.Diagnostics.IsEmpty, string.Join("\n", declared.Diagnostics.Select(d => d.Message)));

            // 经 $eval 执行（counter 仍 0），顶层函数读/写全局
            Assert.Equal(5, engine.Call("Scale", 5));
            Assert.Equal(5, engine.GetGlobal("counter"));

            // 引擎侧写全局 → Co 侧读取
            engine.SetGlobal("counter", 100);
            Assert.Equal(105, engine.Call("Scale", 5));
        }

        [Fact]
        public void IlEmit_CrossSubmission_GlobalPersists()
        {
            // IlEmitter previous 链合并 + IlEmitSession 旧值注入 → 跨提交全局变量持久
            // （与新提交重新初始化的覆盖语义一致；函数跨提交亦可调）。
            using var engine = new CocoaEngine(EngineBackend.IlEmit);
            var first = engine.DoString(@"var counter = 100
function Scale(x: i32): i32 { counter = counter + x; return counter }");
            Assert.True(first.Diagnostics.IsEmpty, string.Join("\n", first.Diagnostics.Select(d => d.Message)));
            Assert.Equal(100, engine.GetGlobal("counter"));

            var second = engine.DoString(@"var other = 1");
            Assert.True(second.Diagnostics.IsEmpty, string.Join("\n", second.Diagnostics.Select(d => d.Message)));

            // 跨提交：变量值保留，函数仍可调
            Assert.Equal(100, engine.GetGlobal("counter"));
            Assert.Equal(105, engine.Call("Scale", 5));
        }

        [Fact]
        public void IlEmit_And_Interpreter_Match_SameScript()
        {
            using var il = new CocoaEngine(EngineBackend.IlEmit);
            using var interp = new CocoaEngine();

            var ilResult = il.DoString(@"function F(x: i32): i32 { return x * x + 1 }
F(5)");
            var interpResult = interp.DoString(@"function F(x: i32): i32 { return x * x + 1 }
F(5)");

            Assert.False(ilResult.Diagnostics.HasErrors(), string.Join("\n", ilResult.Diagnostics.Select(d => d.Message)));
            Assert.False(interpResult.Diagnostics.HasErrors(), string.Join("\n", interpResult.Diagnostics.Select(d => d.Message)));
            Assert.Equal(interpResult.Value, ilResult.Value);
            Assert.Equal(26, ilResult.Value);
        }

        [Fact]
        public void Reset_ClearsSubmissions()
        {
            using var engine = new CocoaEngine(EngineBackend.IlEmit);
            engine.DoString(@"function F(): i32 { return 1 }");
            engine.Reset();
            Assert.Throws<ArgumentException>(() => engine.Call("F"));
        }
    }
}