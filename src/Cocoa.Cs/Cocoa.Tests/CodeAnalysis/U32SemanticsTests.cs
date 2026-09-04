using Cocoa.CodeAnalysis;
using Cocoa.Targeting;
using Cocoa.CodeGen.Native;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.Tests.CodeAnalysis.Emit.Native;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Xunit;

using Cocoa.CodeGen.Managed.Writer;
namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// u32/u64 语义与数组支持锁定——SHA-256 纯 Cocoa 实现（自举缺口分析 P1-8）的地基。
    ///
    /// 语法手册 47.6 只写了 / % 走无符号指令、&gt;&gt; 逻辑右移、比较用无符号条件码，
    /// 未定义溢出行为。实测三后端一致：u32 加法 2^32 环绕（0xffffffffu + 1u = 0），
    /// 这是 SHA-256 的必要性前提；&amp; | ^ ~ &lt;&lt; &gt;&gt; 与 0x…u 大字面量全部正确。
    ///
    /// 另锁定 u32[]/u64[]/u8[] 三后端可用——原先 IL 发射器只放行 int/long/char/byte/
    /// double/bool/enum（IlEmitter.Expressions.cs PrimitiveArrayElementTypeName），
    /// 已补 System.UInt32 / System.UInt64 两行；native 侧元素大小映射本来就齐。
    /// 注意 i32[] 虽可用但 &gt;&gt; 是算术右移，不能装 SHA-256 的 RotR 状态。
    /// </summary>
    public class U32SemanticsTests
    {
        private static string[] References() => new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location };

        private const string Program = @"using System

function Main()
{
    var a: u32 = 0xffffffffu
    var b: u32 = 1u
    var c: u32 = a + b
    Console.WriteLine(""add:"" + c.ToString())

    var d: u32 = 0xbb67ae85u
    Console.WriteLine(""lit:"" + d.ToString())

    var e: u32 = 0x10000u << 8
    Console.WriteLine(""shl:"" + e.ToString())

    var f: u32 = 0x01000000u >> 8
    Console.WriteLine(""shr:"" + f.ToString())

    var g: u32 = 0xff00ff00u ^ 0x0f0f0f0fu
    Console.WriteLine(""xor:"" + g.ToString())

    var h: u32 = 0xff00u | 0x00ffu
    Console.WriteLine(""ror:"" + h.ToString())

    var k: u32 = 0xff00u & 0x0f0fu
    Console.WriteLine(""and:"" + k.ToString())

    var m: u32 = ~0x0000ff00u
    Console.WriteLine(""not:"" + m.ToString())

    var u4: u32[] = new u32[3]
    u4[0] = 0xbb67ae85u
    u4[1] = 0xffffffffu
    Console.WriteLine(""u32arr:"" + u4.Length.ToString())
    Console.WriteLine(""u32val:"" + u4[0].ToString())
    Console.WriteLine(""u32max:"" + u4[1].ToString())
    u4[1] = u4[1] + 1u
    Console.WriteLine(""u32wrap:"" + u4[1].ToString())

    var u6: u64[] = new u64[3]
    u6[0] = 42ul
    Console.WriteLine(""u64arr:"" + u6.Length.ToString())
    Console.WriteLine(""u64val:"" + u6[0].ToString())

    var ub: u8[] = new u8[3]
    ub[0] = 7
    Console.WriteLine(""u8arr:"" + ub.Length.ToString())
    Console.WriteLine(""u8val:"" + ub[0].ToString())

    // SHA-256 用到的具体构造：字节→字打包、旋转右移、u64 中转再窄化
    var ba: u8[] = new u8[4]
    ba[0] = 255
    ba[1] = 0
    ba[2] = 0
    ba[3] = 1
    var packed: u32 = u32((u32(ba[0]) << 24) | (u32(ba[1]) << 16) | (u32(ba[2]) << 8) | u32(ba[3]))
    Console.WriteLine(""pack:"" + packed.ToString())

    var rot: u32 = (0x80000000u >> 1) | (0x80000000u << 31)
    Console.WriteLine(""rot:"" + rot.ToString())

    var wide: u64 = u64(ba[0]) * 16777216ul + u64(ba[3])
    Console.WriteLine(""wide:"" + wide.ToString())
    Console.WriteLine(""back:"" + u32(wide).ToString())

    // 剩余待确认构造：u32 变量作移位量、u32→u8 窄化、u8→u32/u64 升宽
    var w32: u32 = 0xabcd1234u
    var sv: u32 = 16u
    Console.WriteLine(""shvar:"" + (w32 >> sv).ToString())
    var n8: u8 = u8(w32 >> 8)
    Console.WriteLine(""n8:"" + n8.ToString())
    var n8b: u8 = u8(w32)
    Console.WriteLine(""n8b:"" + n8b.ToString())
    Console.WriteLine(""widen:"" + u32(n8b).ToString())
    Console.WriteLine(""widen64:"" + u64(n8b).ToString())
    Console.WriteLine(""notvar:"" + (~w32).ToString())

    // SHA-256 轮函数三形态（σ0 / Σ1 / σ1），验证 u32 变量 + 无后缀移位量 + ^ | 组合可赋 u32 变量
    var sr19: u32 = w32 >> 19
    Console.WriteLine(""sr19:"" + sr19.ToString())
    var sr3: u32 = w32 >> 3
    Console.WriteLine(""sr3:"" + sr3.ToString())
    var sr2: u32 = w32 >> 2
    Console.WriteLine(""sr2:"" + sr2.ToString())

    var x4: u32 = 0xc0000000u
    var s0: u32 = ((x4 >> 7) | (x4 << 25)) ^ ((x4 >> 18) | (x4 << 14)) ^ (x4 >> 3)
    Console.WriteLine(""s0:"" + s0.ToString())
    var S1: u32 = ((x4 >> 6) | (x4 << 26)) ^ ((x4 >> 11) | (x4 << 21)) ^ ((x4 >> 25) | (x4 << 7))
    Console.WriteLine(""S1:"" + S1.ToString())
    var g1: u32 = ((x4 >> 17) | (x4 << 15)) ^ ((x4 >> 19) | (x4 << 13)) ^ (x4 >> 10)
    Console.WriteLine(""g1:"" + g1.ToString())
}";

        private const string Expected =
            "add:0\n" +
            "lit:3144134277\n" +
            "shl:16777216\n" +
            "shr:65536\n" +
            "xor:4027576335\n" +
            "ror:65535\n" +
            "and:3840\n" +
            "not:4294902015\n" +
            "u32arr:3\n" +
            "u32val:3144134277\n" +
            "u32max:4294967295\n" +
            "u32wrap:0\n" +
            "u64arr:3\n" +
            "u64val:42\n" +
            "u8arr:3\n" +
            "u8val:7\n" +
            "pack:4278190081\n" +
            "rot:1073741824\n" +
            "wide:4278190081\n" +
            "back:4278190081\n" +
            "shvar:43981\n" +
            "n8:18\n" +
            "n8b:52\n" +
            "widen:52\n" +
            "widen64:52\n" +
            "notvar:1412623819\n" +
            "sr19:5497\n" +
            "sr3:360292934\n" +
            "sr2:720585869\n" +
            "s0:427831296\n" +
            "S1:51904608\n" +
            "g1:3176448\n";

        [Fact]
        public void Evaluator_U32Semantics()
        {
            using var writer = new StringWriter();
            var original = Console.Out;
            try
            {
                Console.SetOut(writer);
                var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
                var result = compilation.Evaluate(new Dictionary<VariableSymbol, object>());
                Assert.True(!result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics.Select(d => d.Message)));
                Assert.Equal(Expected, (writer.ToString() ?? "").Replace("\r\n", "\n"));
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        private static int RunIl()
        {
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-u32probe", "u32-il-" + Guid.NewGuid().ToString("N") + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var diagnostics = compilation.Emit("u32probe", References(), exePath, IlTarget.Parse("net9.0"));
            Assert.Empty(string.Join("\n", diagnostics));

            var psi = new ProcessStartInfo("dotnet", $"\"{exePath}\"") { RedirectStandardOutput = true, UseShellExecute = false };
            using var process = Process.Start(psi)!;
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new TimeoutException("IL exe did not exit in time.");
            }

            return process.ExitCode;
        }

        private static string RunNative(string target)
        {
            TargetPlatform.TryParse(target, out var platform);
            var compilation = Compilation.Create("Main", References(), SyntaxTree.Parse(Program));
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-u32probe", "u32-native-" + Guid.NewGuid().ToString("N") + "-" + target + ".exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
            var diagnostics = compilation.EmitNative("u32probe", exePath, platform);
            Assert.Empty(string.Join("\n", diagnostics));
            return NativeEmitTests.Run(exePath).Replace("\r\n", "\n");
        }

        [Fact]
        public void IlE2e_U32Semantics() => Assert.Equal(0, RunIl());

        [Theory]
        [InlineData("windows-x64")]
        [InlineData("windows-x86")]
        public void NativeE2e_U32Semantics(string target) => Assert.Equal(Expected, RunNative(target));
    }
}
