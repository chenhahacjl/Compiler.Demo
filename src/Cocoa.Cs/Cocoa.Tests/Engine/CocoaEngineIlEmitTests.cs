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
        public void IlEmit_GlobalVariables_NotSupported()
        {
            using var engine = new CocoaEngine(EngineBackend.IlEmit);
            engine.DoString(@"var counter = 0");
            Assert.Throws<NotSupportedException>(() => engine.GetGlobal("counter"));
            Assert.Throws<NotSupportedException>(() => engine.SetGlobal("counter", 1));
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