using System;
using System.Collections.Generic;

using Cocoa.CodeGen.Native.Assembler;
using Cocoa.CodeGen.PE;

using Cocoa.CodeAnalysis;


namespace Cocoa.CodeGen.Native.Assembler.X64
{

    public sealed partial class X64Assembler : IAssembler
    {
        public void Mov(X64Size size, X64Register dst, X64Register src)
        {
            var opcode = size == X64Size.Byte ? (byte)0x8A : X64EncodingTable.Mov.OpR;
            EmitRegReg(opcode, size, dst, src);
        }

        public void Mov(X64Size size, X64Register dst, X64MemoryOperand src)
        {
            if (size == X64Size.Word) EmitByte(0x66); // operand-size override for 16-bit
            var opcode = size == X64Size.Byte ? (byte)0x8A : X64EncodingTable.Mov.OpR;
            EmitRegMem(opcode, size, dst, src);
        }

        public void Mov(X64Size size, X64MemoryOperand dst, X64Register src)
        {
            if (size == X64Size.Word) EmitByte(0x66); // operand-size override for 16-bit
            var opcode = size == X64Size.Byte ? (byte)0x88 : X64EncodingTable.Mov.OpM;
            EmitMemReg(opcode, size, dst, src);
        }

        public void Mov(X64Size size, X64Register dst, int imm)
        {
            if (size == X64Size.Byte)
            {
                throw new ArgumentException("Byte immediates are not supported.", nameof(size));
            }

            if (size == X64Size.Dword)
            {
                EmitRex(0x40 | ((int)dst >= 8 ? 0x01 : 0));
                EmitByte((byte)(0xB8 + ((int)dst & 7)));
                EmitInt32(imm);
            }
            else
            {
                if (imm >= int.MinValue && imm <= int.MaxValue)
                {
                    EmitRex(0x48 | ((int)dst >= 8 ? 0x01 : 0));
                    EmitByte(0xC7);
                    EmitModRMByte(3, 0, (int)dst & 7);
                    EmitInt32(imm);
                }
                else
                {
                    EmitRex(0x48 | ((int)dst >= 8 ? 0x01 : 0));
                    EmitByte((byte)(0xB8 + ((int)dst & 7)));
                    EmitInt64(imm);
                }
            }
        }

        public void Mov(X64Size size, X64Register dst, long imm)
        {
            if (size == X64Size.Byte)
            {
                throw new ArgumentException("Byte immediates are not supported.", nameof(size));
            }

            if (size == X64Size.Dword)
            {
                Mov(size, dst, (int)imm);
            }
            else
            {
                EmitRex(0x48 | ((int)dst >= 8 ? 0x01 : 0));
                EmitByte((byte)(0xB8 + ((int)dst & 7)));
                EmitInt64(imm);
            }
        }

        public void Mov(X64Register dst, long imm)
        {
            EmitRex(0x48 | ((int)dst >= 8 ? 0x01 : 0));
            EmitByte((byte)(0xB8 + ((int)dst & 7)));
            EmitInt64(imm);
        }

        public void Mov(X64Size size, X64MemoryOperand dst, int imm)
        {
            if (size == X64Size.Byte)
            {
                throw new ArgumentException("Byte immediates are not supported.", nameof(size));
            }

            if (size == X64Size.Qword && (imm < int.MinValue || imm > int.MaxValue))
            {
                throw new ArgumentException("Qword memory immediates must fit in 32 bits.", nameof(imm));
            }

            var memory = EncodeMemory(dst);
            EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | memory.RexB);
            EmitByte(0xC7);
            EmitModRMByte(memory.Mod, 0, memory.Rm);
            EmitMemoryRest(dst, memory);
            EmitInt32(imm);
        }

        public void MovRip(X64Size size, X64Register dst, int symbol)
        {
            EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)dst >= 8 ? 0x04 : 0));
            EmitByte(0x8B);
            EmitModRMByte(0, (int)dst & 7, 5);
            _dataFixups.Add((Position, symbol));
            EmitInt32(0);
        }

        public void Add(X64Size size, X64Register dst, X64Register src) => EmitRmReg(X64EncodingTable.Add.OpM, size, dst, src);
        public void Add(X64Size size, X64Register dst, X64MemoryOperand src) => EmitRegMem(X64EncodingTable.Add.OpR, size, dst, src);
        public void Add(X64Size size, X64MemoryOperand dst, X64Register src) => EmitMemReg(X64EncodingTable.Add.OpM, size, dst, src);
        public void Add(X64Size size, X64Register dst, int imm) => EmitRegImm(X64EncodingTable.Add.Digit, size, dst, imm);

        public void Sub(X64Size size, X64Register dst, X64Register src) => EmitRmReg(X64EncodingTable.Sub.OpM, size, dst, src);
        public void Sub(X64Size size, X64Register dst, X64MemoryOperand src) => EmitRegMem(X64EncodingTable.Sub.OpR, size, dst, src);
        public void Sub(X64Size size, X64MemoryOperand dst, X64Register src) => EmitMemReg(X64EncodingTable.Sub.OpM, size, dst, src);
        public void Sub(X64Size size, X64Register dst, int imm) => EmitRegImm(X64EncodingTable.Sub.Digit, size, dst, imm);

        public void And(X64Size size, X64Register dst, X64Register src) => EmitRmReg(X64EncodingTable.And.OpM, size, dst, src);
        public void And(X64Size size, X64Register dst, X64MemoryOperand src) => EmitRegMem(X64EncodingTable.And.OpR, size, dst, src);
        public void And(X64Size size, X64MemoryOperand dst, X64Register src) => EmitMemReg(X64EncodingTable.And.OpM, size, dst, src);
        public void And(X64Size size, X64Register dst, int imm) => EmitRegImm(X64EncodingTable.And.Digit, size, dst, imm);

        public void Or(X64Size size, X64Register dst, X64Register src) => EmitRmReg(X64EncodingTable.Or.OpM, size, dst, src);
        public void Or(X64Size size, X64Register dst, X64MemoryOperand src) => EmitRegMem(X64EncodingTable.Or.OpR, size, dst, src);
        public void Or(X64Size size, X64MemoryOperand dst, X64Register src) => EmitMemReg(X64EncodingTable.Or.OpM, size, dst, src);
        public void Or(X64Size size, X64Register dst, int imm) => EmitRegImm(X64EncodingTable.Or.Digit, size, dst, imm);

        public void Xor(X64Size size, X64Register dst, X64Register src) => EmitRmReg(X64EncodingTable.Xor.OpM, size, dst, src);
        public void Xor(X64Size size, X64Register dst, X64MemoryOperand src) => EmitRegMem(X64EncodingTable.Xor.OpR, size, dst, src);
        public void Xor(X64Size size, X64MemoryOperand dst, X64Register src) => EmitMemReg(X64EncodingTable.Xor.OpM, size, dst, src);
        public void Xor(X64Size size, X64Register dst, int imm) => EmitRegImm(X64EncodingTable.Xor.Digit, size, dst, imm);

        public void Cmp(X64Size size, X64Register dst, X64Register src) => EmitRmReg(X64EncodingTable.Cmp.OpM, size, dst, src);
        public void Cmp(X64Size size, X64Register dst, X64MemoryOperand src) => EmitRegMem(X64EncodingTable.Cmp.OpR, size, dst, src);
        public void Cmp(X64Size size, X64MemoryOperand dst, X64Register src) => EmitMemReg(X64EncodingTable.Cmp.OpM, size, dst, src);
        public void Cmp(X64Size size, X64Register dst, int imm) => EmitRegImm(X64EncodingTable.Cmp.Digit, size, dst, imm);

        public void Test(X64Size size, X64Register r1, X64Register r2)
        {
            EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)r2 >= 8 ? 0x04 : 0) | ((int)r1 >= 8 ? 0x01 : 0));
            EmitByte(0x85);
            EmitModRMByte(3, (int)r2 & 7, (int)r1 & 7);
        }

        public void Imul(X64Size size, X64Register dst, X64Register src) => EmitExtRegReg(X64ExtTable.Imul, size, dst, src);

        public void Not(X64Size size, X64Register dst) => EmitF7Grp(X64GrpTable.Not.F7, size, dst);

        public void Neg(X64Size size, X64Register dst) => EmitF7Grp(X64GrpTable.Neg.F7, size, dst);

        public void Shl(X64Size size, X64Register dst, int count) => EmitShiftImm(X64GrpTable.Shl.C1, size, dst, count);

        public void Shr(X64Size size, X64Register dst, int count) => EmitShiftImm(X64GrpTable.Shr.C1, size, dst, count);

        public void Sar(X64Size size, X64Register dst, int count) => EmitShiftImm(X64GrpTable.Sar.C1, size, dst, count);

        public void Shl(X64Size size, X64Register dst) => EmitShiftCl(X64GrpTable.Shl.D3, size, dst);

        public void Shr(X64Size size, X64Register dst) => EmitShiftCl(X64GrpTable.Shr.D3, size, dst);

        public void Sar(X64Size size, X64Register dst) => EmitShiftCl(X64GrpTable.Sar.D3, size, dst);

        public void Div(X64Size size, X64Register divisor) => EmitF7Grp(X64GrpTable.Div.F7, size, divisor);

        public void Idiv(X64Size size, X64Register divisor) => EmitF7Grp(X64GrpTable.Idiv.F7, size, divisor);

        // ------------------------------------------------------------------
        // 64 位整型辅助（long，6e-M19 M1）：x64 主路径为 qword 单指令，
        // 以下仅 Adc/Sbb/Shld/Shrd/Mul 备用；x87 FPU 转换在 x64 上走 SSE，不支持。
        // ------------------------------------------------------------------

        public void Mul(X64Size size, X64Register divisor) => EmitF7Grp(X64GrpTable.Mul.F7, size, divisor);

        public void Adc(X64Size size, X64Register dst, X64Register src) => EmitRmReg(X64EncodingTable.Adc.OpM, size, dst, src);
        public void Adc(X64Size size, X64Register dst, X64MemoryOperand src) => EmitRegMem(X64EncodingTable.Adc.OpR, size, dst, src);
        public void Sbb(X64Size size, X64Register dst, X64Register src) => EmitRmReg(X64EncodingTable.Sbb.OpM, size, dst, src);
        public void Sbb(X64Size size, X64Register dst, X64MemoryOperand src) => EmitRegMem(X64EncodingTable.Sbb.OpR, size, dst, src);

        public void AdcRegImm(X64Register dst, int imm)
        {
            EmitRex(0x40 | ((int)dst >= 8 ? 0x01 : 0));
            if (imm is >= -128 and <= 127)
            {
                EmitByte(0x83);
                EmitModRMByte(3, 2, (int)dst & 7);
                EmitByte(unchecked((byte)(sbyte)imm));
            }
            else
            {
                EmitByte(0x81);
                EmitModRMByte(3, 2, (int)dst & 7);
                EmitInt32(imm);
            }
        }

        public void ShldCl(X64Register dst, X64Register src)
        {
            EmitRex(0x40 | ((int)dst >= 8 ? 0x01 : 0) | ((int)src >= 8 ? 0x04 : 0));
            EmitByte(0x0F);
            EmitByte(0xA5);
            EmitModRMByte(3, (int)src & 7, (int)dst & 7);
        }

        public void ShldImm8(X64Register dst, X64Register src, byte count)
        {
            EmitRex(0x40 | ((int)dst >= 8 ? 0x01 : 0) | ((int)src >= 8 ? 0x04 : 0));
            EmitByte(0x0F);
            EmitByte(0xA4);
            EmitModRMByte(3, (int)src & 7, (int)dst & 7);
            EmitByte(count);
        }

        public void ShrdCl(X64Register dst, X64Register src)
        {
            EmitRex(0x40 | ((int)dst >= 8 ? 0x01 : 0) | ((int)src >= 8 ? 0x04 : 0));
            EmitByte(0x0F);
            EmitByte(0xAD);
            EmitModRMByte(3, (int)src & 7, (int)dst & 7);
        }

        public void ShrdImm8(X64Register dst, X64Register src, byte count)
        {
            EmitRex(0x40 | ((int)dst >= 8 ? 0x01 : 0) | ((int)src >= 8 ? 0x04 : 0));
            EmitByte(0x0F);
            EmitByte(0xAC);
            EmitModRMByte(3, (int)src & 7, (int)dst & 7);
            EmitByte(count);
        }

        /// <summary>CDQE（x86 的 CDQ 在 x64 对应符号扩展 EAX→RAX；本后端未使用）。</summary>
        public void Cdq() => throw new NotSupportedException("CDQ is not used on x64; use Cqo for 64-bit division.");

        public void FildM64(X64MemoryOperand src) => throw new NotSupportedException("x87 FPU conversions are not used on x64 (SSE2 path).");
        public void FistpM64(X64MemoryOperand dst) => throw new NotSupportedException("x87 FPU conversions are not used on x64 (SSE2 path).");
        public void FstpM64(X64MemoryOperand dst) => throw new NotSupportedException("x87 FPU conversions are not used on x64 (SSE2 path).");
        public void FldM64(X64MemoryOperand src) => throw new NotSupportedException("x87 FPU conversions are not used on x64 (SSE2 path).");
        public void FstpM32(X64MemoryOperand dst) => throw new NotSupportedException("x87 FPU conversions are not used on x64 (SSE2 path).");
        public void FldM32(X64MemoryOperand src) => throw new NotSupportedException("x87 FPU conversions are not used on x64 (SSE2 path).");
        public void FildM32(X64MemoryOperand src) => throw new NotSupportedException("x87 FPU conversions are not used on x64 (SSE2 path).");
        public void FldcwM16(X64MemoryOperand src) => throw new NotSupportedException("x87 FPU conversions are not used on x64 (SSE2 path).");
        public void FnstcwM16(X64MemoryOperand dst) => throw new NotSupportedException("x87 FPU conversions are not used on x64 (SSE2 path).");
        public void Fmulp() => throw new NotSupportedException("x87 FPU conversions are not used on x64 (SSE2 path).");
        public void Faddp() => throw new NotSupportedException("x87 FPU conversions are not used on x64 (SSE2 path).");

        public void Movzx(X64Size dstSize, X64Register dst, X64Register src) => EmitExtRegReg(X64ExtTable.MovzxB, dstSize, dst, src);

        public void Movzx(X64Size dstSize, X64Register dst, X64MemoryOperand src) => EmitExtRegMem(dstSize == X64Size.Byte ? X64ExtTable.MovzxB : X64ExtTable.MovzxW, dstSize, dst, src);

        public void Movsxd(X64Register dst, X64Register src) => EmitExtRegReg(X64ExtTable.Movsxd, X64Size.Dword, dst, src);

        /// <summary>CQO：RDX:RAX ← 符号扩展 RAX（64 位有符号除法前置）。</summary>
        public void Cqo()
        {
            EmitRex(0x48);
            EmitByte(0x99);
        }

        public void Lea(X64Register dst, X64MemoryOperand src)
        {
            var memory = EncodeMemory(src);
            EmitRex(0x48 | ((int)dst >= 8 ? 0x04 : 0) | memory.RexB);
            EmitByte(0x8D);
            EmitModRMByte(memory.Mod, (int)dst & 7, memory.Rm);
            EmitMemoryRest(src, memory);
        }

        public void LeaRip(X64Register dst, int symbol)
        {
            EmitRex(0x48 | ((int)dst >= 8 ? 0x04 : 0));
            EmitByte(0x8D);
            EmitModRMByte(0, (int)dst & 7, 5);
            _dataFixups.Add((Position, symbol));
            EmitInt32(0);
        }

        public void Push(X64Register reg)
        {
            if ((int)reg >= 8)
            {
                EmitByte(0x41);
            }

            EmitByte((byte)(0x50 + ((int)reg & 7)));
        }

        public void Push(int imm)
        {
            EmitByte(0x68);
            EmitInt32(imm);
        }

        public void Pop(X64Register reg)
        {
            if ((int)reg >= 8)
            {
                EmitByte(0x41);
            }

            EmitByte((byte)(0x58 + ((int)reg & 7)));
        }

        public void Jmp(int label)
        {
            EmitByte(0xE9);
            _labelFixups.Add((Position, label));
            EmitInt32(0);
        }

        public void Jcc(X64CondCode cond, int label)
        {
            EmitByte(0x0F);
            EmitByte(JccOpcode(cond));
            _labelFixups.Add((Position, label));
            EmitInt32(0);
        }

        public void Call(int label)
        {
            EmitByte(0xE8);
            _labelFixups.Add((Position, label));
            EmitInt32(0);
        }

        public void Call(X64Register reg)
        {
            EmitRex(0x40 | ((int)reg >= 8 ? 0x01 : 0));
            EmitByte(0xFF);
            EmitModRMByte(3, 2, (int)reg & 7);
        }

        public void MovGs(X64Register dst, int displacement)
        {
            EmitByte(0x65);
            EmitRex(0x48 | ((int)dst >= 8 ? 0x04 : 0));
            EmitByte(0x8B);
            EmitModRMByte(0, (int)dst & 7, 4);
            EmitByte(0x25);
            EmitInt32(displacement);
        }

        public void CallRip(int symbol)
        {
            EmitByte(0xFF);
            EmitByte(0x15);
            _dataFixups.Add((Position, symbol));
            EmitInt32(0);
        }

        public void Setcc(X64CondCode cond, X64Register dst)
        {
            EmitRex(0x40 | ((int)dst >= 8 ? 0x01 : 0));
            EmitByte(0x0F);
            EmitByte(SetccOpcode(cond));
            EmitModRMByte(3, 0, (int)dst & 7);
        }

        public void Ret()
        {
            EmitByte(0xC3);
        }

        public void Nop()
        {
            EmitByte(0x90);
        }

        // ------------------------------------------------------------------
        // SSE（double，IEEE-754 binary64）
        // ------------------------------------------------------------------

        public void Movsd(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(X64SseTable.Movsd.Op, X64SseTable.Movsd.Prefix, xmmDst, xmmSrc);
        public void Movsd(X64Register xmmDst, X64MemoryOperand src) => EmitSseRegMem(X64SseTable.Movsd.Op, X64SseTable.Movsd.Prefix, xmmDst, src);
        public void Movsd(X64MemoryOperand dst, X64Register xmmSrc) => EmitSseMemReg(X64SseTable.Movsd.OpStore, X64SseTable.Movsd.Prefix, dst, xmmSrc);
        public void Addsd(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(X64SseTable.Addsd.Op, X64SseTable.Addsd.Prefix, xmmDst, xmmSrc);
        public void Subsd(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(X64SseTable.Subsd.Op, X64SseTable.Subsd.Prefix, xmmDst, xmmSrc);
        public void Mulsd(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(X64SseTable.Mulsd.Op, X64SseTable.Mulsd.Prefix, xmmDst, xmmSrc);
        public void Divsd(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(X64SseTable.Divsd.Op, X64SseTable.Divsd.Prefix, xmmDst, xmmSrc);
        public void Sqrtsd(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(X64SseTable.Sqrtsd.Op, X64SseTable.Sqrtsd.Prefix, xmmDst, xmmSrc);
        public void Roundsd(X64Register xmmDst, X64Register xmmSrc, byte imm) => EmitSseRegImm(X64SseTable.Roundsd.Op, X64SseTable.Roundsd.Prefix, xmmDst, xmmSrc, imm);
        public void Cvtsi2sd(X64Register xmmDst, X64Register r32Src) => EmitSseRegReg(X64SseTable.Cvtsi2sd.Op, X64SseTable.Cvtsi2sd.Prefix, xmmDst, r32Src);
        public void Cvttsd2si(X64Register r32Dst, X64Register xmmSrc) => EmitSseRegReg(X64SseTable.Cvttsd2si.Op, X64SseTable.Cvttsd2si.Prefix, r32Dst, xmmSrc);
        public void Cvtsi2sd64(X64Register xmmDst, X64Register r64Src) => EmitSseRegReg(X64SseTable.Cvtsi2sd64.Op, X64SseTable.Cvtsi2sd64.Prefix, xmmDst, r64Src, rexW: true);
        public void Cvttsd2si64(X64Register r64Dst, X64Register xmmSrc) => EmitSseRegReg(X64SseTable.Cvttsd2si64.Op, X64SseTable.Cvttsd2si64.Prefix, r64Dst, xmmSrc, rexW: true);
        public void Ucomisd(X64Register xmmA, X64Register xmmB) => EmitSseRegReg(X64SseTable.Ucomisd.Op, X64SseTable.Ucomisd.Prefix, xmmA, xmmB);
        public void MovdGprToXmm(X64Register xmmDst, X64Register r32Src) => EmitSseRegReg(0x6E, 0x66, xmmDst, r32Src);
        public void MovdXmmToGpr(X64Register r32Dst, X64Register xmmSrc) => EmitSseRegReg(0x7E, 0x66, xmmSrc, r32Dst);
        public void MovqGprToXmm(X64Register xmmDst, X64Register r64Src) => EmitSseRegReg(0x6E, 0x66, xmmDst, r64Src, rexW: true);
        public void MovqXmmToGpr(X64Register r64Dst, X64Register xmmSrc) => EmitSseRegReg(0xD6, 0x66, xmmSrc, r64Dst, rexW: true);
        public void Pinsrd(X64Register xmmDst, X64Register r32Src, byte imm) => EmitSseRegImm(0x22, 0x66, xmmDst, r32Src, imm);
        public void Pextrd(X64Register r32Dst, X64Register xmmSrc, byte imm) => EmitSseRegImm(0x16, 0x66, r32Dst, xmmSrc, imm);

        // ------------------------------------------------------------------
        // SSE（float 单精度，IEEE-754 binary32，前缀 F3）
        // ------------------------------------------------------------------

        public void Movss(X64Register xmmDst, X64MemoryOperand src) => EmitSseRegMem(X64SseTable.Movss.Op, X64SseTable.Movss.Prefix, xmmDst, src);
        public void Movss(X64MemoryOperand dst, X64Register xmmSrc) => EmitSseMemReg(X64SseTable.Movss.OpStore, X64SseTable.Movss.Prefix, dst, xmmSrc);
        public void Addss(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(X64SseTable.Addss.Op, X64SseTable.Addss.Prefix, xmmDst, xmmSrc);
        public void Subss(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(X64SseTable.Subss.Op, X64SseTable.Subss.Prefix, xmmDst, xmmSrc);
        public void Mulss(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(X64SseTable.Mulss.Op, X64SseTable.Mulss.Prefix, xmmDst, xmmSrc);
        public void Divss(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(X64SseTable.Divss.Op, X64SseTable.Divss.Prefix, xmmDst, xmmSrc);
        public void Sqrtss(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(X64SseTable.Sqrtss.Op, X64SseTable.Sqrtss.Prefix, xmmDst, xmmSrc);
        public void Roundss(X64Register xmmDst, X64Register xmmSrc, byte imm) => EmitSseRegImm(X64SseTable.Roundss.Op, X64SseTable.Roundss.Prefix, xmmDst, xmmSrc, imm);
        public void Cvtsi2ss(X64Register xmmDst, X64Register r32Src) => EmitSseRegReg(X64SseTable.Cvtsi2ss.Op, X64SseTable.Cvtsi2ss.Prefix, xmmDst, r32Src);
        public void Cvttss2si(X64Register r32Dst, X64Register xmmSrc) => EmitSseRegReg(X64SseTable.Cvttss2si.Op, X64SseTable.Cvttss2si.Prefix, r32Dst, xmmSrc);
        public void Cvtss2sd(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(X64SseTable.Cvtss2sd.Op, X64SseTable.Cvtss2sd.Prefix, xmmDst, xmmSrc);
        public void Cvtsd2ss(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(X64SseTable.Cvtsd2ss.Op, X64SseTable.Cvtsd2ss.Prefix, xmmDst, xmmSrc);

        public void Ucomiss(X64Register xmmA, X64Register xmmB)
        {
            EmitByte(0x0F); // UCOMISS 无前缀
            EmitByte(0x2E);
            EmitModRMByte(3, (int)xmmA & 7, (int)xmmB & 7);
        }

        public void MovssRip(X64Register xmmDst, int symbol)
        {
            EmitRex(0x40 | (((int)xmmDst & 8) != 0 ? 0x04 : 0));
            EmitByte(0xF3);
            EmitByte(0x0F);
            EmitByte(0x10);
            EmitModRMByte(0, (int)xmmDst & 7, 5);
            _dataFixups.Add((Position, symbol));
            EmitInt32(0);
        }

        public void MovsdRip(X64Register xmmDst, int symbol)
        {
            EmitRex(0x40 | (((int)xmmDst & 8) != 0 ? 0x04 : 0));
            EmitByte(0xF2);
            EmitByte(0x0F);
            EmitByte(0x10);
            EmitModRMByte(0, (int)xmmDst & 7, 5);
            _dataFixups.Add((Position, symbol));
            EmitInt32(0);
        }

        private static byte JccOpcode(X64CondCode cond) => X64CondTable.ByCond(cond).Jcc;

        private static byte SetccOpcode(X64CondCode cond) => X64CondTable.ByCond(cond).Setcc;

    }
}
