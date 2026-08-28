using System;
using System.Collections.Generic;

using Cocoa.CodeAnalysis.Emit.Native.Assembler;
using Cocoa.CodeAnalysis.Emit.Native.PEFile;

namespace Cocoa.CodeAnalysis.Emit.Native.Assembler.X64
{

    internal sealed partial class X64Assembler : IAssembler
    {
        public void Mov(X64Size size, X64Register dst, X64Register src)
        {
            var opcode = size == X64Size.Byte ? (byte)0x8A : (byte)0x8B;
            EmitRegReg(opcode, size, dst, src);
        }

        public void Mov(X64Size size, X64Register dst, X64MemoryOperand src)
        {
            if (size == X64Size.Word) EmitByte(0x66); // operand-size override for 16-bit
            var opcode = size == X64Size.Byte ? (byte)0x8A : (byte)0x8B;
            EmitRegMem(opcode, size, dst, src);
        }

        public void Mov(X64Size size, X64MemoryOperand dst, X64Register src)
        {
            if (size == X64Size.Word) EmitByte(0x66); // operand-size override for 16-bit
            var opcode = size == X64Size.Byte ? (byte)0x88 : (byte)0x89;
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

        public void Add(X64Size size, X64Register dst, X64Register src) => EmitRmReg(0x01, size, dst, src);
        public void Add(X64Size size, X64Register dst, X64MemoryOperand src) => EmitRegMem(0x03, size, dst, src);
        public void Add(X64Size size, X64MemoryOperand dst, X64Register src) => EmitMemReg(0x01, size, dst, src);
        public void Add(X64Size size, X64Register dst, int imm) => EmitRegImm(0, size, dst, imm);

        public void Sub(X64Size size, X64Register dst, X64Register src) => EmitRmReg(0x29, size, dst, src);
        public void Sub(X64Size size, X64Register dst, X64MemoryOperand src) => EmitRegMem(0x2B, size, dst, src);
        public void Sub(X64Size size, X64MemoryOperand dst, X64Register src) => EmitMemReg(0x29, size, dst, src);
        public void Sub(X64Size size, X64Register dst, int imm) => EmitRegImm(5, size, dst, imm);

        public void And(X64Size size, X64Register dst, X64Register src) => EmitRmReg(0x21, size, dst, src);
        public void And(X64Size size, X64Register dst, X64MemoryOperand src) => EmitRegMem(0x23, size, dst, src);
        public void And(X64Size size, X64MemoryOperand dst, X64Register src) => EmitMemReg(0x21, size, dst, src);
        public void And(X64Size size, X64Register dst, int imm) => EmitRegImm(4, size, dst, imm);

        public void Or(X64Size size, X64Register dst, X64Register src) => EmitRmReg(0x09, size, dst, src);
        public void Or(X64Size size, X64Register dst, X64MemoryOperand src) => EmitRegMem(0x0B, size, dst, src);
        public void Or(X64Size size, X64MemoryOperand dst, X64Register src) => EmitMemReg(0x09, size, dst, src);
        public void Or(X64Size size, X64Register dst, int imm) => EmitRegImm(1, size, dst, imm);

        public void Xor(X64Size size, X64Register dst, X64Register src) => EmitRmReg(0x31, size, dst, src);
        public void Xor(X64Size size, X64Register dst, X64MemoryOperand src) => EmitRegMem(0x33, size, dst, src);
        public void Xor(X64Size size, X64MemoryOperand dst, X64Register src) => EmitMemReg(0x31, size, dst, src);
        public void Xor(X64Size size, X64Register dst, int imm) => EmitRegImm(6, size, dst, imm);

        public void Cmp(X64Size size, X64Register dst, X64Register src) => EmitRmReg(0x39, size, dst, src);
        public void Cmp(X64Size size, X64Register dst, X64MemoryOperand src) => EmitRegMem(0x3B, size, dst, src);
        public void Cmp(X64Size size, X64MemoryOperand dst, X64Register src) => EmitMemReg(0x39, size, dst, src);
        public void Cmp(X64Size size, X64Register dst, int imm) => EmitRegImm(7, size, dst, imm);

        public void Test(X64Size size, X64Register r1, X64Register r2)
        {
            EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)r2 >= 8 ? 0x04 : 0) | ((int)r1 >= 8 ? 0x01 : 0));
            EmitByte(0x85);
            EmitModRMByte(3, (int)r2 & 7, (int)r1 & 7);
        }

        public void Imul(X64Size size, X64Register dst, X64Register src)
        {
            EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)dst >= 8 ? 0x04 : 0) | ((int)src >= 8 ? 0x01 : 0));
            EmitByte(0x0F);
            EmitByte(0xAF);
            EmitModRMByte(3, (int)dst & 7, (int)src & 7);
        }

        public void Not(X64Size size, X64Register dst)
        {
            EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)dst >= 8 ? 0x01 : 0));
            EmitByte(0xF7);
            EmitModRMByte(3, 2, (int)dst & 7);
        }

        public void Neg(X64Size size, X64Register dst)
        {
            EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)dst >= 8 ? 0x01 : 0));
            EmitByte(0xF7);
            EmitModRMByte(3, 3, (int)dst & 7);
        }

        public void Shl(X64Size size, X64Register dst, int count)
        {
            EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)dst >= 8 ? 0x01 : 0));
            EmitByte(0xC1);
            EmitModRMByte(3, 4, (int)dst & 7);
            EmitByte((byte)count);
        }

        public void Shr(X64Size size, X64Register dst, int count)
        {
            EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)dst >= 8 ? 0x01 : 0));
            EmitByte(0xC1);
            EmitModRMByte(3, 5, (int)dst & 7);
            EmitByte((byte)count);
        }

        public void Sar(X64Size size, X64Register dst, int count)
        {
            EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)dst >= 8 ? 0x01 : 0));
            EmitByte(0xC1);
            EmitModRMByte(3, 7, (int)dst & 7);
            EmitByte((byte)count);
        }

        public void Shl(X64Size size, X64Register dst)
        {
            EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)dst >= 8 ? 0x01 : 0));
            EmitByte(0xD3);
            EmitModRMByte(3, 4, (int)dst & 7);
        }

        public void Shr(X64Size size, X64Register dst)
        {
            EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)dst >= 8 ? 0x01 : 0));
            EmitByte(0xD3);
            EmitModRMByte(3, 5, (int)dst & 7);
        }

        public void Sar(X64Size size, X64Register dst)
        {
            EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)dst >= 8 ? 0x01 : 0));
            EmitByte(0xD3);
            EmitModRMByte(3, 7, (int)dst & 7);
        }

        public void Div(X64Size size, X64Register divisor)
        {
            EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)divisor >= 8 ? 0x01 : 0));
            EmitByte(0xF7);
            EmitModRMByte(3, 6, (int)divisor & 7);
        }

        public void Idiv(X64Size size, X64Register divisor)
        {
            EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)divisor >= 8 ? 0x01 : 0));
            EmitByte(0xF7);
            EmitModRMByte(3, 7, (int)divisor & 7);
        }

        // ------------------------------------------------------------------
        // 64 位整型辅助（long，6e-M19 M1）：x64 主路径为 qword 单指令，
        // 以下仅 Adc/Sbb/Shld/Shrd/Mul 备用；x87 FPU 转换在 x64 上走 SSE，不支持。
        // ------------------------------------------------------------------

        public void Mul(X64Size size, X64Register divisor)
        {
            EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)divisor >= 8 ? 0x01 : 0));
            EmitByte(0xF7);
            EmitModRMByte(3, 4, (int)divisor & 7);
        }

        public void Adc(X64Size size, X64Register dst, X64Register src) => EmitRmReg(0x11, size, dst, src);
        public void Adc(X64Size size, X64Register dst, X64MemoryOperand src) => EmitRegMem(0x13, size, dst, src);
        public void Sbb(X64Size size, X64Register dst, X64Register src) => EmitRmReg(0x19, size, dst, src);
        public void Sbb(X64Size size, X64Register dst, X64MemoryOperand src) => EmitRegMem(0x1B, size, dst, src);

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

        public void Movzx(X64Size dstSize, X64Register dst, X64Register src)
        {
            EmitRex(0x40 | (dstSize == X64Size.Qword ? 0x08 : 0) | ((int)dst >= 8 ? 0x04 : 0) | ((int)src >= 8 ? 0x01 : 0));
            EmitByte(0x0F);
            EmitByte(0xB6);
            EmitModRMByte(3, (int)dst & 7, (int)src & 7);
        }

        public void Movzx(X64Size dstSize, X64Register dst, X64MemoryOperand src)
        {
            var memory = EncodeMemory(src);
            EmitRex(0x40 | (dstSize == X64Size.Qword ? 0x08 : 0) | ((int)dst >= 8 ? 0x04 : 0) | memory.RexB);
            EmitByte(0x0F);
            EmitByte((byte)(dstSize == X64Size.Byte ? 0xB6 : 0xB7));
            EmitModRMByte(memory.Mod, (int)dst & 7, memory.Rm);
            EmitMemoryRest(src, memory);
        }

        public void Movsxd(X64Register dst, X64Register src)
        {
            EmitRex(0x48 | ((int)dst >= 8 ? 0x04 : 0) | ((int)src >= 8 ? 0x01 : 0));
            EmitByte(0x63);
            EmitModRMByte(3, (int)dst & 7, (int)src & 7);
        }

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

        public void Movsd(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(0x10, 0xF2, xmmDst, xmmSrc);
        public void Movsd(X64Register xmmDst, X64MemoryOperand src) => EmitSseRegMem(0x10, 0xF2, xmmDst, src);
        public void Movsd(X64MemoryOperand dst, X64Register xmmSrc) => EmitSseMemReg(0x11, 0xF2, dst, xmmSrc);
        public void Addsd(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(0x58, 0xF2, xmmDst, xmmSrc);
        public void Subsd(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(0x5C, 0xF2, xmmDst, xmmSrc);
        public void Mulsd(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(0x59, 0xF2, xmmDst, xmmSrc);
        public void Divsd(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(0x5E, 0xF2, xmmDst, xmmSrc);
        public void Sqrtsd(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(0x51, 0xF2, xmmDst, xmmSrc);
        public void Roundsd(X64Register xmmDst, X64Register xmmSrc, byte imm) => EmitSseRegImm(0x0B, 0x66, xmmDst, xmmSrc, imm);
        public void Cvtsi2sd(X64Register xmmDst, X64Register r32Src) => EmitSseRegReg(0x2A, 0xF2, xmmDst, r32Src);
        public void Cvttsd2si(X64Register r32Dst, X64Register xmmSrc) => EmitSseRegReg(0x2C, 0xF2, r32Dst, xmmSrc);
        public void Cvtsi2sd64(X64Register xmmDst, X64Register r64Src) => EmitSseRegReg(0x2A, 0xF2, xmmDst, r64Src, rexW: true);
        public void Cvttsd2si64(X64Register r64Dst, X64Register xmmSrc) => EmitSseRegReg(0x2C, 0xF2, r64Dst, xmmSrc, rexW: true);
        public void Ucomisd(X64Register xmmA, X64Register xmmB) => EmitSseRegReg(0x2E, 0x66, xmmA, xmmB);
        public void MovdGprToXmm(X64Register xmmDst, X64Register r32Src) => EmitSseRegReg(0x6E, 0x66, xmmDst, r32Src);
        public void MovdXmmToGpr(X64Register r32Dst, X64Register xmmSrc) => EmitSseRegReg(0x7E, 0x66, xmmSrc, r32Dst);
        public void MovqGprToXmm(X64Register xmmDst, X64Register r64Src) => EmitSseRegReg(0x6E, 0x66, xmmDst, r64Src, rexW: true);
        public void MovqXmmToGpr(X64Register r64Dst, X64Register xmmSrc) => EmitSseRegReg(0xD6, 0x66, xmmSrc, r64Dst, rexW: true);
        public void Pinsrd(X64Register xmmDst, X64Register r32Src, byte imm) => EmitSseRegImm(0x22, 0x66, xmmDst, r32Src, imm);
        public void Pextrd(X64Register r32Dst, X64Register xmmSrc, byte imm) => EmitSseRegImm(0x16, 0x66, r32Dst, xmmSrc, imm);

        // ------------------------------------------------------------------
        // SSE（float 单精度，IEEE-754 binary32，前缀 F3）
        // ------------------------------------------------------------------

        public void Movss(X64Register xmmDst, X64MemoryOperand src) => EmitSseRegMem(0x10, 0xF3, xmmDst, src);
        public void Movss(X64MemoryOperand dst, X64Register xmmSrc) => EmitSseMemReg(0x11, 0xF3, dst, xmmSrc);
        public void Addss(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(0x58, 0xF3, xmmDst, xmmSrc);
        public void Subss(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(0x5C, 0xF3, xmmDst, xmmSrc);
        public void Mulss(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(0x59, 0xF3, xmmDst, xmmSrc);
        public void Divss(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(0x5E, 0xF3, xmmDst, xmmSrc);
        public void Sqrtss(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(0x51, 0xF3, xmmDst, xmmSrc);
        public void Roundss(X64Register xmmDst, X64Register xmmSrc, byte imm) => EmitSseRegImm(0x0B, 0xF3, xmmDst, xmmSrc, imm);
        public void Cvtsi2ss(X64Register xmmDst, X64Register r32Src) => EmitSseRegReg(0x2A, 0xF3, xmmDst, r32Src);
        public void Cvttss2si(X64Register r32Dst, X64Register xmmSrc) => EmitSseRegReg(0x2C, 0xF3, r32Dst, xmmSrc);
        public void Cvtss2sd(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(0x5A, 0xF3, xmmDst, xmmSrc);
        public void Cvtsd2ss(X64Register xmmDst, X64Register xmmSrc) => EmitSseRegReg(0x5A, 0xF2, xmmDst, xmmSrc);

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

        private static byte JccOpcode(X64CondCode cond) => cond switch
        {
            X64CondCode.Equal => 0x84,
            X64CondCode.NotEqual => 0x85,
            X64CondCode.Below => 0x82,
            X64CondCode.BelowOrEqual => 0x86,
            X64CondCode.Above => 0x87,
            X64CondCode.AboveOrEqual => 0x83,
            X64CondCode.Less => 0x8C,
            X64CondCode.LessOrEqual => 0x8E,
            X64CondCode.Greater => 0x8F,
            X64CondCode.GreaterOrEqual => 0x8D,
            X64CondCode.Parity => 0x8A,
            X64CondCode.NoParity => 0x8B,
            _ => throw new ArgumentOutOfRangeException(nameof(cond)),
        };

        private static byte SetccOpcode(X64CondCode cond) => cond switch
        {
            X64CondCode.Equal => 0x94,
            X64CondCode.NotEqual => 0x95,
            X64CondCode.Below => 0x92,
            X64CondCode.BelowOrEqual => 0x96,
            X64CondCode.Above => 0x97,
            X64CondCode.AboveOrEqual => 0x93,
            X64CondCode.Less => 0x9C,
            X64CondCode.LessOrEqual => 0x9E,
            X64CondCode.Greater => 0x9F,
            X64CondCode.GreaterOrEqual => 0x9D,
            X64CondCode.Parity => 0x9A,
            X64CondCode.NoParity => 0x9B,
            _ => throw new ArgumentOutOfRangeException(nameof(cond)),
        };

    }
}
