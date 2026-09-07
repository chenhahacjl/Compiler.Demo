using System;
using System.Linq;
using Cocoa.CodeGen.Native.Assembler;
using Cocoa.CodeGen.Native.Assembler.X64;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.Native
{
    /// <summary>
    /// X64Assembler 指令编码字节断言（9d：汇编器可信化，纯托管不启动 native exe）。
    /// 覆盖本次 FileStream/native 修复触及的核心编码：
    ///   · LoadSlot/StoreSlot 8 字节（REX.W：mov [rbp-x],rax 零扩展写满 8 字节）
    ///   · SysCall 参数装载（mov rcx,rax）与第 5 参（mov [rsp+0x20],rax）
    ///   · x86 8 字节返回双槽写（mov [rbp-x],eax / mov [rbp-(x+4)],edx，见 EmitSysCall x86 分支）
    ///   · 帧调整 sub rsp,imm 与 i32→i64 符号扩展 movsxd
    /// 权威编码来源：Intel SDM / objdump 实测（调试期间与生成产物逐字节对照）。
    /// </summary>
    public class X64AssemblerEncodingTests
    {
        private static byte[] Emit(Action<IAssembler> body)
        {
            var a = new X64Assembler();
            body(a);
            return a.ToArray();
        }

        private static void AssertHex(string expectedHex, byte[] actual)
        {
            var expected = expectedHex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => Convert.ToByte(t, 16)).ToArray();
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void Push_Rbp_Ret()
        {
            AssertHex("55", Emit(a => a.Push(X64Register.RBP)));
            AssertHex("C3", Emit(a => a.Ret()));
        }

        [Fact]
        public void LoadSlot_Qword_MovRaxMemRbp8()
        {
            // LoadSlot(EAX, reg, 8)：mov rax, qword [rbp-8] → 48 8B 45 F8
            AssertHex("48 8B 45 F8", Emit(a => a.Mov(X64Size.Qword, X64Register.RAX, new X64MemoryOperand(X64Register.RBP, -8))));
        }

        [Fact]
        public void StoreSlot_Qword_MovMemRbp8Rax()
        {
            // StoreSlot(8字节, EAX)：mov qword [rbp-8], rax → 48 89 45 F8（REX.W 写满 8 字节）
            AssertHex("48 89 45 F8", Emit(a => a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RBP, -8), X64Register.RAX)));
        }

        [Fact]
        public void StoreSlot_Dword_MovMemRbp8Eax()
        {
            // x86 8 字节返回低 dword 槽：mov dword [rbp-8], eax → 89 45 F8（无 REX.W）
            AssertHex("89 45 F8", Emit(a => a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, -8), X64Register.EAX)));
        }

        [Fact]
        public void StoreSlot_Dword_HighSlot_Edx()
        {
            // x86 8 字节返回高 dword 槽（slot-4）：mov dword [rbp-12], edx → 89 55 F4
            AssertHex("89 55 F4", Emit(a => a.Mov(X64Size.Dword, new X64MemoryOperand(X64Register.RBP, -12), X64Register.EDX)));
        }

        [Fact]
        public void SysCallArg0_MovRcxRax()
        {
            // 参数装载：mov rcx, rax（0x8B 方向：rcx←rax，reg=rcx rm=rax）→ 48 8B C8
            AssertHex("48 8B C8", Emit(a => a.Mov(X64Size.Qword, X64Register.RCX, X64Register.RAX)));
        }

        [Fact]
        public void SysCallArg5_StackSlot()
        {
            // 第 5 参：[rsp+0x20] ← rax → 48 89 44 24 20
            AssertHex("48 89 44 24 20", Emit(a => a.Mov(X64Size.Qword, new X64MemoryOperand(X64Register.RSP, 0x20), X64Register.RAX)));
        }

        [Fact]
        public void MovDwordImm_ConstI32()
        {
            // mov eax, 0 → B8 00 00 00 00
            AssertHex("B8 00 00 00 00", Emit(a => a.Mov(X64Size.Dword, X64Register.EAX, 0)));
        }

        [Fact]
        public void SubRspImm_FrameAdjust()
        {
            // sub rsp, 0x30 → 48 83 EC 30
            AssertHex("48 83 EC 30", Emit(a => a.Sub(X64Size.Qword, X64Register.RSP, 0x30)));
        }

        [Fact]
        public void Movsxd_I32ToI64()
        {
            // movsxd rax, eax → 48 63 C0
            AssertHex("48 63 C0", Emit(a => a.Movsxd(X64Register.RAX, X64Register.EAX)));
        }

        [Fact]
        public void LoadSlot_Dword_MovEaxMemRbp16()
        {
            // LoadSlot(EAX, reg, 4)：mov eax, [rbp-0x10] → 8B 45 F0
            AssertHex("8B 45 F0", Emit(a => a.Mov(X64Size.Dword, X64Register.EAX, new X64MemoryOperand(X64Register.RBP, -0x10))));
        }

        [Fact]
        public void MovQwordImm64_Movabs()
        {
            // movabs rax, 0x123456789 → 48 B8 89 67 45 23 01 00 00 00
            AssertHex("48 B8 89 67 45 23 01 00 00 00", Emit(a => a.Mov(X64Size.Qword, X64Register.RAX, 0x123456789L)));
        }
    }
}