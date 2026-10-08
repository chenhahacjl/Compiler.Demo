using System;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Cocoa.Tests.Compiler
{
    /// <summary>带参实例构造器（public constructor(a, b)）：newobj → 真实 ctor 行 → 字段初始化 → 方法读取。</summary>
    public class CtorParamProbe
    {
        private readonly ITestOutputHelper _out;
        public CtorParamProbe(ITestOutputHelper o) { _out = o; }

        [Fact]
        public void InstanceCtor_WithParams_InitializesFields_AndRuns()
        {
            var nl = Environment.NewLine;
            var hex = SelfHostedEndToEndTests.RunSelfDriver(
                "class P {" + nl +
                "    private field _v: i32" + nl +
                "    private field _s: string" + nl +
                "    public constructor(a: i32, b: string) {" + nl +
                "        _v = a" + nl +
                "        _s = b" + nl +
                "    }" + nl +
                "    public function Get(): i32 { return _v }" + nl +
                "    public function Len(): i32 { return _s.Length }" + nl +
                "}" + nl +
                "function Main(args: string[]): i32 {" + nl +
                "    var p = new P(7, \"abc\")" + nl +
                "    return p.Get() + p.Len()" + nl +
                "}" + nl);
            Assert.False(hex.StartsWith("ERR:", StringComparison.Ordinal), "自编失败: " + hex);
            var dir = Path.Combine(Path.GetTempPath(), "cocoa-ctorprobe");
            Directory.CreateDirectory(dir);
            var dll = Path.Combine(dir, "T.dll");
            File.WriteAllBytes(dll, SelfHostedEndToEndTests.HexToBytes(hex));
            var asm = System.Reflection.Assembly.LoadFile(dll);
            var pType = asm.GetType("P");
            Assert.NotNull(pType);
            var ctor = pType!.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly).Single();
            Assert.Equal(new[] { "Int32", "String" }, ctor.GetParameters().Select(p => p.ParameterType.Name).ToArray());
            // 7 + "abc".Length(3) = 10
            Assert.Equal(10, (int)asm.EntryPoint!.Invoke(null, new object[] { new[] { "x" } })!);
        }
    }
}
