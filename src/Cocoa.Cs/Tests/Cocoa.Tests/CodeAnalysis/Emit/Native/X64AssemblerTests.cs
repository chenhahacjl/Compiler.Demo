using Cocoa.CodeGen.Native.Assembler.X64;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native
{
    public class X64AssemblerTests
    {
        [Fact]
        public void Mov_RegReg_EncodesCorrectly()
        {
            var a = Assemble(x =>
            {
                x.Mov(X64Size.Dword, X64Register.RAX, X64Register.RAX);
                x.Mov(X64Size.Qword, X64Register.RAX, X64Register.RBX);
                x.Mov(X64Size.Qword, X64Register.R8, X64Register.RAX);
                x.Mov(X64Size.Dword, X64Register.RBX, X64Register.RAX);
            });

            Assert.Equal("8B C0 48 8B C3 4C 8B C0 8B D8", Hex(a));
        }

        [Fact]
        public void Mov_RegImm_EncodesCorrectly()
        {
            var a = Assemble(x =>
            {
                x.Mov(X64Size.Dword, X64Register.RAX, 1);
                x.Mov(X64Size.Qword, X64Register.RAX, 1);
                x.Mov(X64Size.Qword, X64Register.RAX, long.MaxValue);
                x.Mov(X64Size.Dword, X64Register.R8, 256);
            });

            Assert.Equal("B8 01 00 00 00 48 C7 C0 01 00 00 00 48 B8 FF FF FF FF FF FF FF 7F 41 B8 00 01 00 00", Hex(a));
        }

        [Fact]
        public void Mov_RegMem_EncodesCorrectly()
        {
            var a = Assemble(x =>
            {
                x.Mov(X64Size.Qword, X64Register.RAX, new X64MemoryOperand(X64Register.RBP, -8));
                x.Mov(X64Size.Dword, X64Register.RAX, new X64MemoryOperand(X64Register.RBP, -8));
                x.Mov(X64Size.Qword, X64Register.RAX, new X64MemoryOperand(X64Register.RDX, 0x12345678));
                x.Mov(X64Size.Qword, X64Register.RAX, new X64MemoryOperand(X64Register.RSP, 16));
                x.Mov(X64Size.Qword, X64Register.RAX, new X64MemoryOperand(X64Register.R12, 16));
            });

            Assert.Equal("48 8B 45 F8 8B 45 F8 48 8B 82 78 56 34 12 48 8B 44 24 10 49 8B 44 24 10", Hex(a));
        }

        [Fact]
        public void Mov_MemReg_EncodesCorrectly()
        {
            var a = Assemble(x =>
            {
                x.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RBP, -8), X64Register.RAX);
                x.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RBP, 0), X64Register.RAX);
                x.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RDX, 0), X64Register.RAX);
                x.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RSP, 16), X64Register.RAX);
            });

            Assert.Equal("48 89 45 F8 48 89 45 00 48 89 02 48 89 44 24 10", Hex(a));
        }

        [Fact]
        public void Mov_MemImm_EncodesCorrectly()
        {
            var a = Assemble(x =>
            {
                x.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, -8), 5);
                x.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RBP, -8), 5);
                x.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RBP, 0x12345678), -1);
            });

            Assert.Equal("C7 45 F8 05 00 00 00 48 C7 45 F8 05 00 00 00 48 C7 85 78 56 34 12 FF FF FF FF", Hex(a));
        }

        [Fact]
        public void Mov_Byte_EncodesCorrectly()
        {
            var a = Assemble(x =>
            {
                x.Mov(X64Size.Byte, X64Register.RAX, X64Register.RCX);
                x.Mov(X64Size.Byte, new X64MemoryOperand(X64Register.RBP, -1), X64Register.R8);
            });

            Assert.Equal("8A C1 44 88 45 FF", Hex(a));
        }

        [Fact]
        public void Arithmetic_EncodesCorrectly()
        {
            var a = Assemble(x =>
            {
                x.Add(X64Size.Qword, X64Register.RAX, X64Register.RCX);
                x.Add(X64Size.Dword, X64Register.RAX, X64Register.RCX);
                x.Add(X64Size.Qword, X64Register.RAX, 5);
                x.Add(X64Size.Qword, X64Register.RAX, 300);
                x.Add(X64Size.Qword, new X64MemoryOperand(X64Register.RBP, -8), X64Register.RAX);
                x.Add(X64Size.Qword, X64Register.RAX, new X64MemoryOperand(X64Register.RBP, -8));
                x.Sub(X64Size.Qword, X64Register.RAX, X64Register.RBX);
                x.Cmp(X64Size.Dword, X64Register.RAX, X64Register.RBX);
                x.And(X64Size.Qword, X64Register.RAX, X64Register.RCX);
                x.Or(X64Size.Qword, X64Register.RAX, X64Register.RCX);
                x.Xor(X64Size.Qword, X64Register.RAX, X64Register.RCX);
                x.Xor(X64Size.Dword, X64Register.RAX, X64Register.RAX);
            });

            Assert.Equal("48 01 C8 01 C8 48 83 C0 05 48 81 C0 2C 01 00 00 48 01 45 F8 48 03 45 F8 48 29 D8 39 D8 48 21 C8 48 09 C8 48 31 C8 31 C0", Hex(a));
        }

        [Fact]
        public void Test_Imul_Not_Neg_EncodesCorrectly()
        {
            var a = Assemble(x =>
            {
                x.Test(X64Size.Qword, X64Register.RAX, X64Register.RAX);
                x.Imul(X64Size.Qword, X64Register.RCX, X64Register.RAX);
                x.Not(X64Size.Qword, X64Register.RAX);
                x.Neg(X64Size.Qword, X64Register.RAX);
                x.Neg(X64Size.Dword, X64Register.RAX);
            });

            Assert.Equal("48 85 C0 48 0F AF C8 48 F7 D0 48 F7 D8 F7 D8", Hex(a));
        }

        [Fact]
        public void Shifts_EncodesCorrectly()
        {
            var a = Assemble(x =>
            {
                x.Shl(X64Size.Qword, X64Register.RAX, 2);
                x.Shl(X64Size.Dword, X64Register.RAX, 2);
                x.Shr(X64Size.Qword, X64Register.RAX, 2);
                x.Sar(X64Size.Qword, X64Register.RAX, 2);
                x.Shl(X64Size.Qword, X64Register.RAX);
            });

            Assert.Equal("48 C1 E0 02 C1 E0 02 48 C1 E8 02 48 C1 F8 02 48 D3 E0", Hex(a));
        }

        [Fact]
        public void Movzx_Movsxd_EncodesCorrectly()
        {
            var a = Assemble(x =>
            {
                x.Movzx(X64Size.Dword, X64Register.RAX, X64Register.RAX);
                x.Movzx(X64Size.Qword, X64Register.RAX, X64Register.RAX);
                x.Movzx(X64Size.Dword, X64Register.RAX, X64Register.RCX);
                x.Movsxd(X64Register.RAX, X64Register.RAX);
            });

            Assert.Equal("0F B6 C0 48 0F B6 C0 0F B6 C1 48 63 C0", Hex(a));
        }

        [Fact]
        public void Lea_EncodesCorrectly()
        {
            var a = Assemble(x =>
            {
                x.Lea(X64Register.RAX, new X64MemoryOperand(X64Register.RBP, -8));
                x.Lea(X64Register.R8, new X64MemoryOperand(X64Register.RBP, 16));
            });

            Assert.Equal("48 8D 45 F8 4C 8D 45 10", Hex(a));
        }

        [Fact]
        public void PushPop_EncodesCorrectly()
        {
            var a = Assemble(x =>
            {
                x.Push(X64Register.RBP);
                x.Pop(X64Register.RBP);
                x.Push(X64Register.R12);
                x.Pop(X64Register.R12);
                x.Push(X64Register.RAX);
            });

            Assert.Equal("55 5D 41 54 41 5C 50", Hex(a));
        }

        [Fact]
        public void Jmp_ResolvesRelativeDisplacement()
        {
            var a = Assemble(x =>
            {
                var b = x.CreateLabel();
                x.Jmp(b);
                x.Ret();
                x.MarkLabel(b);
            });

            Assert.Equal("E9 01 00 00 00 C3", Hex(a));
        }

        [Fact]
        public void Jcc_ResolvesRelativeDisplacement()
        {
            var a = Assemble(x =>
            {
                var b = x.CreateLabel();
                x.Jcc(X64CondCode.Equal, b);
                x.Ret();
                x.Nop();
                x.MarkLabel(b);
                x.Ret();
            });

            Assert.Equal("0F 84 02 00 00 00 C3 90 C3", Hex(a));
        }

        [Fact]
        public void Call_ResolvesRelativeDisplacement()
        {
            var a = Assemble(x =>
            {
                var target = x.CreateLabel();
                x.Call(target);
                x.Nop();
                x.MarkLabel(target);
            });

            Assert.Equal("E8 01 00 00 00 90", Hex(a));
        }

        [Fact]
        public void Setcc_EncodesCorrectly()
        {
            var a = Assemble(x =>
            {
                x.Setcc(X64CondCode.NotEqual, X64Register.RAX);
                x.Setcc(X64CondCode.Equal, X64Register.R8);
                x.Setcc(X64CondCode.Greater, X64Register.RCX);
            });

            Assert.Equal("0F 95 C0 41 0F 94 C0 0F 9F C1", Hex(a));
        }

        [Fact]
        public void Ret_Nop_EncodeCorrectly()
        {
            var a = Assemble(x =>
            {
                x.Ret();
                x.Nop();
            });

            Assert.Equal("C3 90", Hex(a));
        }

        [Fact]
        public void HighRegisters_UseRex()
        {
            var a = Assemble(x =>
            {
                x.Add(X64Size.Qword, X64Register.R8, X64Register.R9);
                x.Sub(X64Size.Dword, X64Register.R9, X64Register.R8);
                x.Cmp(X64Size.Qword, X64Register.R11, X64Register.R15);
            });

            Assert.Equal("4D 01 C8 45 29 C1 4D 39 FB", Hex(a));
        }

        [Fact]
        public void DataSymbol_ResolvesRipRelativeLea()
        {
            var a = Assemble(x =>
            {
                x.WriteDataBytes(0x01, 0x02);
                var s = x.CreateDataSymbol();
                x.MarkDataSymbol(s);
                x.LeaRip(X64Register.RAX, s);
                x.MovRip(X64Size.Qword, X64Register.R8, s);
            });

            a.Patch(0x1000, 0);

            Assert.Equal("48 8D 05 FB 0F 00 00 4C 8B 05 F4 0F 00 00", Hex(a));
            Assert.Equal(new byte[] { 0x01, 0x02 }, a.GetData());
        }

        [Fact]
        public void AlignData_PadsWithZeros()
        {
            var a = new X64Assembler();

            a.WriteDataBytes(0xAA, 0xBB, 0xCC);
            a.AlignData(4);

            Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0x00 }, a.GetData());
        }

        [Fact]
        public void MissingLabel_Throws()
        {
            var a = new X64Assembler();

            a.Jmp(a.CreateLabel());

            Assert.Throws<InvalidOperationException>(() => a.Patch(0, 0));
        }

        private static X64Assembler Assemble(Action<X64Assembler> build)
        {
            var assembler = new X64Assembler();
            build(assembler);
            assembler.Patch(0, 0);
            return assembler;
        }

        private static string Hex(X64Assembler assembler)
        {
            return string.Join(" ", assembler.ToArray().Select(b => b.ToString("X2")));
        }
    }
}