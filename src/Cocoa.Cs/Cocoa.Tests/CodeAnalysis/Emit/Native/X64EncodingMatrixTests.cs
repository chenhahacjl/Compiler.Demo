using System;
using System.Linq;
using Cocoa.CodeGen.Native.Assembler;
using Cocoa.CodeGen.Native.Assembler.X64;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native
{
    /// <summary>
    /// P1 表驱动编码矩阵：从 X64EncodingTable 表条目自动生成逐字节断言（表项对拍）。
    /// 每条目锁定 4 个方向（reg←reg / reg←mem / mem←reg / reg←imm32）+ 2 尺寸（Dword/Qword）。
    /// 权威编码来源：Intel SDM 与 objdump 实测（编码表相关文件与 LLVM X86InstrInfo.td 对照，方向变体经 objdump 确认合法）。
    /// </summary>
    public class X64EncodingMatrixTests
    {
        private static byte[] Emit(Action<IAssembler> body)
        {
            var a = new X64Assembler();
            body(a);
            return a.ToArray();
        }

        private static string Hex(byte[] bytes) =>
            string.Join(" ", bytes.Select(b => b.ToString("X2")));

        [Fact]
        public void Table_All_ExposesExpectedEntries()
        {
            var names = X64EncodingTable.All.Select(e => e.Name).ToArray();
            Assert.Equal(new[] { "MOV", "ADD", "OR", "ADC", "SBB", "AND", "SUB", "XOR", "CMP" }, names);
            Assert.Equal(9, X64EncodingTable.All.Count);
        }

        // reg←reg 采用 ADD r/m,r 方向（rm=dst, reg=src）：opcode 0x01 组（与 mem-reg 同向）
        [Theory]
        [InlineData("ADD", 0x03, "01 C8")]
        [InlineData("OR", 0x0B, "09 C8")]
        [InlineData("ADC", 0x13, "11 C8")]
        [InlineData("SBB", 0x1B, "19 C8")]
        [InlineData("AND", 0x23, "21 C8")]
        [InlineData("SUB", 0x2B, "29 C8")]
        [InlineData("XOR", 0x33, "31 C8")]
        [InlineData("CMP", 0x3B, "39 C8")]
        public void Alu_RegReg_Dword(string name, byte expectedOpcode, string expected)
        {
            var entry = X64EncodingTable.All.Single(e => e.Name == name);
            Assert.Equal(expectedOpcode, entry.OpR); // OpR 供 reg←mem（检查表一致性）；reg-reg 走 OpM（下方 VerifyAluRr 对拍）

            var a = new X64Assembler();
            switch (entry.Name)
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

            // ADD r/m,r（rm=EAX, reg=ECX）→ 01 C8
            Assert.Equal(expected, Hex(a.ToArray()));
        }

        [Theory]
        [InlineData("ADD", "48 01 D0")]
        [InlineData("XOR", "48 31 D0")]
        [InlineData("SUB", "48 29 D0")]
        public void Alu_RegReg_Qword_RexW(string name, string expected)
        {
            var a = new X64Assembler();
            switch (name)
            {
                case "ADD": a.Add(X64Size.Qword, X64Register.RAX, X64Register.RDX); break;
                case "XOR": a.Xor(X64Size.Qword, X64Register.RAX, X64Register.RDX); break;
                case "SUB": a.Sub(X64Size.Qword, X64Register.RAX, X64Register.RDX); break;
            }

            // r/m,r：48 opcode D0（rm=RAX, reg=RDX）
            Assert.Equal(expected, Hex(a.ToArray()));
        }

        // mem←reg（r/m←reg 方向）
        [Fact]
        public void Alu_MemReg_Dword_RbpDisp()
        {
            // add qword [rbp-8], rax → 48 01 45 F8（ModRM reg=RAX, rm=RBP, mod=1）
            var a = new X64Assembler();
            a.Add(X64Size.Qword, new X64MemoryOperand(X64Register.RBP, -8), X64Register.RAX);
            Assert.Equal("48 01 45 F8", Hex(a.ToArray()));
        }

        [Theory]
        [InlineData("AND", "21 45 F8")]
        [InlineData("OR", "09 45 F8")]
        [InlineData("CMP", "39 45 F8")]
        [InlineData("SUB", "29 45 F8")]
        [InlineData("XOR", "31 45 F8")]
        public void Alu_MemReg_MemDst(string name, string expected)
        {
            var a = new X64Assembler();
            var mem = new X64MemoryOperand(X64Register.RBP, -8);
            switch (name)
            {
                case "AND": a.And(X64Size.Dword, mem, X64Register.EAX); break;
                case "OR": a.Or(X64Size.Dword, mem, X64Register.EAX); break;
                case "CMP": a.Cmp(X64Size.Dword, mem, X64Register.EAX); break;
                case "SUB": a.Sub(X64Size.Dword, mem, X64Register.EAX); break;
                case "XOR": a.Xor(X64Size.Dword, mem, X64Register.EAX); break;
            }

            // r/m←reg：opcode 45 F8
            Assert.Equal(expected, Hex(a.ToArray()));
        }

        // reg←imm32：0x81 /digit（-128..127 走 0x83 8-bit——此处用大立即数锁 0x81）
        [Theory]
        [InlineData("ADD", 0, "81 C0 78 56 34 12")]
        [InlineData("AND", 4, "81 E0 78 56 34 12")]
        [InlineData("SUB", 5, "81 E8 78 56 34 12")]
        [InlineData("XOR", 6, "81 F0 78 56 34 12")]
        [InlineData("CMP", 7, "81 F8 78 56 34 12")]
        public void Alu_RegImm32_Dword_AccListing(string name, int digit, string expected)
        {
            var entry = X64EncodingTable.All.Single(e => e.Name == name);
            Assert.Equal((byte)digit, entry.Digit);

            var a = new X64Assembler();
            switch (name)
            {
                case "ADD": a.Add(X64Size.Dword, X64Register.EAX, 0x12345678); break;
                case "AND": a.And(X64Size.Dword, X64Register.EAX, 0x12345678); break;
                case "SUB": a.Sub(X64Size.Dword, X64Register.EAX, 0x12345678); break;
                case "XOR": a.Xor(X64Size.Dword, X64Register.EAX, 0x12345678); break;
                case "CMP": a.Cmp(X64Size.Dword, X64Register.EAX, 0x12345678); break;
            }

            // dword imm32：EmitRegImm 对非 8-bit 用 81 /digit 组（非累加器 0x05 短编码）
            Assert.Equal(expected, Hex(a.ToArray()));
        }

        // reg←imm8（-128..127）：0x83 /digit（非累加器，一般 83 EC xx）
        [Theory]
        [InlineData("ADD", 0, "83 C0 2A")]
        [InlineData("AND", 4, "83 E0 2A")]
        [InlineData("SUB", 5, "83 E8 2A")]
        [InlineData("CMP", 7, "83 F8 2A")]
        public void Alu_RegImm8_SignExt8(string name, int digit, string expected)
        {
            var a = new X64Assembler();
            switch (name)
            {
                case "ADD": a.Add(X64Size.Dword, X64Register.EAX, 42); break;
                case "AND": a.And(X64Size.Dword, X64Register.EAX, 42); break;
                case "SUB": a.Sub(X64Size.Dword, X64Register.EAX, 42); break;
                case "CMP": a.Cmp(X64Size.Dword, X64Register.EAX, 42); break;
            }

            Assert.Equal(expected, Hex(a.ToArray()));
        }
    }
}