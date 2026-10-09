using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Cocoa.Metadata;
using Xunit;
using Xunit.Abstractions;

namespace Cocoa.Tests.CodeAnalysis
{
    /// <summary>
    /// IlOpCode 编码表校验：本仓表的取值是**直接写进 PE 的真实 IL 字节**
    /// （<c>IlAssembler</c> 写 <c>opCode.Value</c>），因此必须与 ECMA-335 一致。
    /// 用进程内权威来源 <see cref="OpCodes"/>（CLR 自身）逐条比对，
    /// 既验证既有取值，也为补新指令（如 checked 的 ovf 族）提供正确字节。
    /// </summary>
    public class IlOpCodeTableTests
    {
        private readonly ITestOutputHelper _out;
        public IlOpCodeTableTests(ITestOutputHelper o) { _out = o; }

        /// <summary>本仓名 → CLR 同名指令。用 <see cref="OpCode.Name"/> 精确匹配（大小写敏感）。</summary>
        private static Dictionary<string, short> ClrValues()
        {
            var result = new Dictionary<string, short>();

            foreach (var field in typeof(OpCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (field.FieldType != typeof(OpCode))
                {
                    continue;
                }

                var op = (OpCode)field.GetValue(null)!;
                result[op.Name!] = unchecked((short)op.Value);
            }

            // CLR 的 OpCode.Name 是小写（add / div.un），本仓是 PascalCase（Add / Div_Un）
            return result.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void ArithmeticOpcodes_MatchClrEncodings()
        {
            var clr = ClrValues();

            foreach (var name in new[] { "Add", "Sub", "Mul", "Div", "Rem", "And", "Or", "Xor", "Shl", "Shr", "Neg", "Not" })
            {
                Assert.True(clr.ContainsKey(name), $"CLR 无同名指令 {name}");
                var expected = clr[name];
                var actual = unchecked((short)IlOpCodeTable.Get(name).Value);

                _out.WriteLine($"{name,-6} 本仓=0x{actual:X4} CLR=0x{expected:X4}");
                Assert.True(expected == actual,
                    $"{name} 编码不一致：本仓 0x{actual:X4}，CLR 0x{expected:X4}（值直接写入 PE，非映射）");
            }
        }

        [Fact]
        public void DivUn_RemUn_ShrUn_MatchClrEncodings()
        {
            var clr = ClrValues();

            foreach (var (ours, clrName) in new[] { ("Div_Un", "div.un"), ("Rem_Un", "rem.un"), ("Shr_Un", "shr.un") })
            {
                var actual = unchecked((short)IlOpCodeTable.Get(ours).Value);
                var expected = clr[clrName];

                _out.WriteLine($"{clrName,-8} 本仓={ours} 0x{actual:X4} CLR=0x{expected:X4}");
                Assert.True(expected == actual, $"{clrName} 编码不一致：本仓 0x{actual:X4}，CLR 0x{expected:X4}");
            }
        }

        [Theory]
        [InlineData("Add_Ovf", "add.ovf")]
        [InlineData("Add_Ovf_Un", "add.ovf.un")]
        [InlineData("Sub_Ovf", "sub.ovf")]
        [InlineData("Sub_Ovf_Un", "sub.ovf.un")]
        [InlineData("Mul_Ovf", "mul.ovf")]
        [InlineData("Mul_Ovf_Un", "mul.ovf.un")]
        public void OverflowOpcodes_MatchClrEncodings(string ours, string clrName)
        {
            // checked 上下文所需的 ovf 族：两字节（0xFE 前缀）编码，取值以 CLR 自身为准
            var clr = ClrValues();
            Assert.True(clr.ContainsKey(clrName), $"CLR 无 {clrName}");

            int actual = IlOpCodeTable.Get(ours).Value;
            int expected = clr[clrName];

            _out.WriteLine($"{clrName,-12} 本仓={ours} 0x{actual:X4} CLR=0x{expected:X4}");

            // 逐字节比对 CLR 权威取值；ovf 族是单字节（0xD6–0xDB），不套本仓两字节约定
            Assert.True(expected == actual,
                $"{clrName} 编码不一致：本仓 0x{actual:X4}，CLR 0x{expected:X4}");
            Assert.False(IlOpCodeTable.Get(ours).IsTwoByte, $"{ours} 应为单字节指令（0xD6–0xDB 段）");
            Assert.Equal(1, IlOpCodeTable.Get(ours).Size);
        }

        /// <summary>
        /// checked 上下文端到端：IL 里必须出现**单字节** 0xD6（add.ovf），
        /// 且不得出现 0xFE 0xD6 的两字节形式——那个编码未分配，CLR 会判 InvalidProgramException。
        /// 本例是该坑的回归护栏：曾把 ovf 误按本仓两字节约定写成 0xFED6。
        /// </summary>
        [Theory]
        [InlineData("Ldtoken", "ldtoken")]
        [InlineData("Sizeof", "sizeof")]
        [InlineData("Box", "box")]
        [InlineData("Isinst", "isinst")]
        [InlineData("Unbox_Any", "unbox.any")]
        public void TypeTokenOpcodes_MatchClrEncodings(string ours, string clrName)
        {
            // 类型 token 类指令：操作数是 4 字节元数据 token，编码同样以 CLR 为准
            var clr = ClrValues();
            Assert.True(clr.ContainsKey(clrName), $"CLR 无 {clrName}");

            int actual = IlOpCodeTable.Get(ours).Value;
            int expected = clr[clrName];
            if (expected < 0)
            {
                // ClrValues() 把两字节指令（0xFE 前缀）存成有符号 16 位，统一到 0xFE00|b
                expected = 0xFE00 | (expected & 0xFF);
            }

            _out.WriteLine($"{clrName,-10} 本仓={ours} 0x{actual:X4} CLR=0x{expected:X4}");
            Assert.True(expected == actual, $"{clrName} 编码不一致：本仓 0x{actual:X4}，CLR 0x{expected:X4}");
        }

        [Fact]
        public void Ldtoken_IsDeclaredAsTypeOperand()
        {
            // ldtoken 的操作数语义是「类型 token」，与 isinst/box 同类——
            // 曾误声明为 InlineTok（IL 未定义该操作数类型），也在 StackDelta 中漏了 +1
            Assert.Equal(IlOperandType.InlineType, IlOpCodeTable.Get("Ldtoken").OperandType);
        }

        [Fact]
        public void GeneratedAssembly_CheckedContext_EmitsSingleByteAddOvf()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cocoa-ovf-dump", "Ovf.dll");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

            var compilation = Cocoa.CodeAnalysis.Compilation.Create(Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(
                "function Main(args: string[]): i32 {\n" +
                "    checked {\n" +
                "        var a: i32 = 2147483647\n" +
                "        var b = a + 1\n" +
                "        return b\n" +
                "    }\n" +
                "}\n"));

            var diagnostics = compilation.Emit("Main",
                new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location },
                path, Cocoa.Targeting.IlTarget.Parse("net9.0"), emitLibrary: true);
            Assert.Empty(string.Join("\n", diagnostics.Where(d => d.IsError)));

            var bytes = System.Reflection.Assembly.LoadFile(path).EntryPoint!.GetMethodBody()!.GetILAsByteArray()!;
            _out.WriteLine("Main IL: " + string.Join(" ", bytes.Select(b => b.ToString("X2"))));

            Assert.Contains((byte)0xD6, bytes);

            for (var i = 0; i + 1 < bytes.Length; i++)
            {
                Assert.False(bytes[i] == 0xFE && bytes[i + 1] == 0xD6,
                    "ovf 不得写成两字节形式（FE D6）——该编码非法，CLR 判 InvalidProgramException");
            }
        }
    }
}
