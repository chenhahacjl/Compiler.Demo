using System;
using System.Linq;
using Cocoa.CodeGen.Native.Assembler;
using Cocoa.CodeGen.Native.Assembler.X64;
using Cocoa.CodeGen.Native.Assembler.X86;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native
{
    /// <summary>
    /// X86Assembler 编码字节矩阵（P6：x86 表驱动对称锁定）。
    /// x86 无 REX 前缀（ModRM 直发）、8 字节参数/返回值走双 dword。
    /// 覆盖 SQL：ALU reg-reg / reg-imm8 / reg-imm32、MOV 方向、分组 F7-C1-D3、SSE（F2/F3/66）。
    /// 权威来源：Intel SDM + objdump 实测；与 x64 表条目共享 opcode（X64EncodingTable/X64GrpTable/X64SseTable/X64CondTable）。
    /// </summary>
    public class X86EncodingMatrixTests
    {
        private static byte[] Emit(Action<IAssembler> body)
        {
            var a = new X86Assembler();
            body(a);
            return a.ToArray();
        }

        private static string Hex(byte[] bytes) =>
            string.Join(" ", bytes.Select(b => b.ToString("X2")));

        // ALU reg-reg（r/m,r 方向，rm=dst）：与 x64 同 opcode，无 REX
        [Theory]
        [InlineData("ADD", "01 C8")]
        [InlineData("OR", "09 C8")]
        [InlineData("ADC", "11 C8")]
        [InlineData("SBB", "19 C8")]
        [InlineData("AND", "21 C8")]
        [InlineData("SUB", "29 C8")]
        [InlineData("XOR", "31 C8")]
        [InlineData("CMP", "39 C8")]
        public void Alu_RegReg_Dword(string name, string expected)
        {
            var a = new X86Assembler();
            switch (name)
            {
                case "ADD": a.Add(X64Size.Dword, X64Register.EAX, X64Register.ECX); break;
                case "OR": a.Or(X64Size.Dword, X64Register.EAX, X64Register.ECX); break;
                case "ADC": a.Adc(X64Size.Dword, X64Register.EAX, X64Register.ECX); break;
                case "SBB": a.Sbb(X64Size.Dword, X64Register.EAX, X64Register.ECX); break;
                case "AND": a.And(X64Size.Dword, X64Register.EAX, X64Register.ECX); break;
                case "SUB": a.Sub(X64Size.Dword, X64Register.EAX, X64Register.ECX); break;
                case "XOR": a.Xor(X64Size.Dword, X64Register.EAX, X64Register.ECX); break;
                case "CMP": a.Cmp(X64Size.Dword, X64Register.EAX, X64Register.ECX); break;
            }

            Assert.Equal(expected, Hex(a.ToArray()));
        }

        // ALU reg-imm：8-bit（83 /digit）与 32-bit（81 /digit），无 REX
        [Theory]
        [InlineData("ADD", 42, "83 C0 2A")]
        [InlineData("SUB", 42, "83 E8 2A")]
        [InlineData("AND", 42, "83 E0 2A")]
        [InlineData("ADD", 0x12345678, "81 C0 78 56 34 12")]
        [InlineData("SUB", 0x12345678, "81 E8 78 56 34 12")]
        [InlineData("XOR", 0x12345678, "81 F0 78 56 34 12")]
        public void Alu_RegImm(string name, int imm, string expected)
        {
            var a = new X86Assembler();
            switch (name)
            {
                case "ADD": a.Add(X64Size.Dword, X64Register.EAX, imm); break;
                case "SUB": a.Sub(X64Size.Dword, X64Register.EAX, imm); break;
                case "AND": a.And(X64Size.Dword, X64Register.EAX, imm); break;
                case "XOR": a.Xor(X64Size.Dword, X64Register.EAX, imm); break;
            }

            Assert.Equal(expected, Hex(a.ToArray()));
        }

        // MOV 方向（reg←rm 0x8B / r/m←reg 0x89）
        [Fact]
        public void Mov_RegReg()
        {
            var a = new X86Assembler();
            a.Mov(X64Size.Dword, X64Register.EAX, X64Register.ECX);
            Assert.Equal("8B C1", Hex(a.ToArray()));
        }

        [Fact]
        public void Mov_MemDst_RegDisp8()
        {
            var a = new X86Assembler();
            a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, -8), X64Register.EAX);
            Assert.Equal("89 45 F8", Hex(a.ToArray()));
        }

        [Fact]
        public void Mov_QwordImm_TwoDwordStore()
        {
            // Mov(reg, long) x86：B8+rd + imm32
            var a = new X86Assembler();
            a.Mov(X64Size.Dword, X64Register.EAX, 0x12345678);
            Assert.Equal("B8 78 56 34 12", Hex(a.ToArray()));
        }

        // 分组：F7 单操作数 / C1 移位 imm8 / D3 移位 CL
        [Theory]
        [InlineData("NOT", "F7 D0")]
        [InlineData("NEG", "F7 D8")]
        [InlineData("MUL", "F7 E0")]
        [InlineData("DIV", "F7 F0")]
        [InlineData("IDIV", "F7 F8")]
        public void Grp_F7_Unary(string name, string expected)
        {
            var a = new X86Assembler();
            switch (name)
            {
                case "NOT": a.Not(X64Size.Dword, X64Register.EAX); break;
                case "NEG": a.Neg(X64Size.Dword, X64Register.EAX); break;
                case "MUL": a.Mul(X64Size.Dword, X64Register.EAX); break;
                case "DIV": a.Div(X64Size.Dword, X64Register.EAX); break;
                case "IDIV": a.Idiv(X64Size.Dword, X64Register.EAX); break;
            }

            Assert.Equal(expected, Hex(a.ToArray()));
        }

        [Theory]
        [InlineData("SHL", "C1 E0 02")]
        [InlineData("SHR", "C1 E8 02")]
        [InlineData("SAR", "C1 F8 02")]
        public void Grp_ShiftImm8(string name, string expected)
        {
            var a = new X86Assembler();
            switch (name)
            {
                case "SHL": a.Shl(X64Size.Dword, X64Register.EAX, 2); break;
                case "SHR": a.Shr(X64Size.Dword, X64Register.EAX, 2); break;
                case "SAR": a.Sar(X64Size.Dword, X64Register.EAX, 2); break;
            }

            Assert.Equal(expected, Hex(a.ToArray()));
        }

        [Theory]
        [InlineData("SHL", "D3 E0")]
        [InlineData("SHR", "D3 E8")]
        [InlineData("SAR", "D3 F8")]
        public void Grp_ShiftCl(string name, string expected)
        {
            var a = new X86Assembler();
            switch (name)
            {
                case "SHL": a.Shl(X64Size.Dword, X64Register.EAX); break;
                case "SHR": a.Shr(X64Size.Dword, X64Register.EAX); break;
                case "SAR": a.Sar(X64Size.Dword, X64Register.EAX); break;
            }

            Assert.Equal(expected, Hex(a.ToArray()));
        }

        // SSE：无 REX（xmm0-7 直接 ModRM）
        [Theory]
        [InlineData("MOVSD", "F2 0F 10 C1")]
        [InlineData("ADDSD", "F2 0F 58 C1")]
        [InlineData("MULSD", "F2 0F 59 C1")]
        [InlineData("SQRTSD", "F2 0F 51 C1")]
        [InlineData("UCOMISD", "66 0F 2E C1")]
        [InlineData("CVTSI2SD", "F2 0F 2A C0")]
        public void Sse_RegReg(string name, string expected)
        {
            var a = new X86Assembler();
            switch (name)
            {
                case "MOVSD": a.Movsd(X64Register.XMM0, X64Register.XMM1); break;
                case "ADDSD": a.Addsd(X64Register.XMM0, X64Register.XMM1); break;
                case "MULSD": a.Mulsd(X64Register.XMM0, X64Register.XMM1); break;
                case "SQRTSD": a.Sqrtsd(X64Register.XMM0, X64Register.XMM1); break;
                case "UCOMISD": a.Ucomisd(X64Register.XMM0, X64Register.XMM1); break;
                case "CVTSI2SD": a.Cvtsi2sd(X64Register.XMM0, X64Register.EAX); break;
            }

            Assert.Equal(expected, Hex(a.ToArray()));
        }

        [Fact]
        public void Sse_Movss_Store_Mem()
        {
            var a = new X86Assembler();
            a.Movss(new X64MemoryOperand(X64Register.RBP, -8), X64Register.XMM0);
            Assert.Equal("F3 0F 11 45 F8", Hex(a.ToArray()));
        }

        // 条件码：Jcc（0F 8x + rel32 fixup）/ Setcc（0F 9x）
        [Fact]
        public void Cond_Jcc_Rel32()
        {
            var a = new X86Assembler();
            a.Jcc(X64CondCode.Equal, 0);
            Assert.Equal("0F 84 00 00 00 00", Hex(a.ToArray()));
        }

        [Fact]
        public void Cond_Setcc_Al()
        {
            var a = new X86Assembler();
            a.Setcc(X64CondCode.Less, X64Register.EAX);
            Assert.Equal("0F 9C C0", Hex(a.ToArray()));
        }
    }
}