using System;
using System.Collections.Generic;

using Cocoa.CodeGen.Native.Assembler.X64;
using Cocoa.CodeGen.PE;

using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit;

namespace Cocoa.CodeGen.Native.Assembler.X86
{
    /// <summary>
    /// 32 锟?x86 姹囩紪鍣ㄣ€備笌 X64Assembler 鍏辩敤瀵勫瓨锟?灏哄鏋氫妇锟?
    ///  - 浠呬綆 8 涓瘎瀛樺櫒鍙敤锛圧AX..RDI锛夛紝锟?8 涓姏寮傚父
    ///  - X64Size.Qword 闈欓粯闄嶇骇锟?32 浣嶏紙鎸囬拡瀹藉害 4 瀛楄妭锟?
    ///  - 数据引用（MovRip/LeaRip/CallRip）使用绝对地址 [disp32] 而非 RIP 相对
    /// </summary>
    internal sealed class X86Assembler : AssemblerBase, IAssembler
    {
        public override void Patch(int dataTextDelta, long imageBase)
        {
            foreach (var fixup in _labelFixups)
            {
                if (!_labels.TryGetValue(fixup.Label, out var target))
                {
                    throw new InvalidOperationException($"Label {fixup.Label} was never marked.");
                }

                WriteInt32At(fixup.Offset, target - (fixup.Offset + 4));
            }

            foreach (var fixup in _dataFixups)
            {
                if (!_dataOffsets.TryGetValue(fixup.Symbol, out var dataOffset))
                {
                    throw new InvalidOperationException($"Data symbol {fixup.Symbol} was never marked.");
                }

                WriteInt32At(fixup.Offset, (int)checked(imageBase + PeFileWriter.TextRva + dataTextDelta + dataOffset));
            }

            // M4a：数据段内绝对地址（VA）——vtable 槽 → 代码 / 名字指针 → 数据（x86 指针 4 字节）
            foreach (var fixup in _dataCodeFixups)
            {
                if (!_labels.TryGetValue(fixup.Label, out var labelOffset))
                {
                    throw new InvalidOperationException($"Label {fixup.Label} was never marked.");
                }

                WriteDataInt32At(fixup.DataOffset, checked(imageBase + PeFileWriter.TextRva + labelOffset));
            }

            foreach (var fixup in _dataDataFixups)
            {
                if (!_dataOffsets.TryGetValue(fixup.Symbol, out var dataOffset))
                {
                    throw new InvalidOperationException($"Data symbol {fixup.Symbol} was never marked.");
                }

                WriteDataInt32At(fixup.DataOffset, checked(imageBase + dataTextDelta + PeFileWriter.TextRva + dataOffset));
            }
        }

        private void WriteDataInt32At(int offset, long value)
        {
            _data[offset] = unchecked((byte)value);
            _data[offset + 1] = unchecked((byte)(value >> 8));
            _data[offset + 2] = unchecked((byte)(value >> 16));
            _data[offset + 3] = unchecked((byte)(value >> 24));
        }

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

            EmitByte((byte)(0xB8 + ((int)dst & 7)));
            EmitInt32(imm);
        }

        public void Mov(X64Size size, X64Register dst, long imm)
        {
            if (size == X64Size.Byte)
            {
                throw new ArgumentException("Byte immediates are not supported.", nameof(size));
            }

            Mov(size, dst, (int)imm);
        }

        public void Mov(X64Register dst, long imm)
        {
            if (imm < int.MinValue || imm > int.MaxValue)
            {
                throw new ArgumentException("x86 cannot move 64-bit immediates.", nameof(imm));
            }

            EmitByte((byte)(0xB8 + ((int)dst & 7)));
            EmitInt32((int)imm);
        }

        public void Mov(X64Size size, X64MemoryOperand dst, int imm)
        {
            if (size == X64Size.Byte)
            {
                throw new ArgumentException("Byte immediates are not supported.", nameof(size));
            }

            var memory = EncodeMemory(dst);
            EmitByte(0xC7);
            EmitModRMByte(memory.Mod, 0, memory.Rm);
            EmitMemoryRest(dst, memory);
            EmitInt32(imm);
        }

        public void MovRip(X64Size size, X64Register dst, int symbol)
        {
            if (size == X64Size.Word) EmitByte(0x66);
            var opcode = size == X64Size.Byte ? (byte)0x8A : (byte)0x8B;
            EmitByte(opcode);
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
            EmitByte(0x85);
            EmitModRMByte(3, (int)r2 & 7, (int)r1 & 7);
        }

        public void Imul(X64Size size, X64Register dst, X64Register src)
        {
            EmitByte(0x0F);
            EmitByte(0xAF);
            EmitModRMByte(3, (int)dst & 7, (int)src & 7);
        }

        public void Not(X64Size size, X64Register dst)
        {
            EmitByte(0xF7);
            EmitModRMByte(3, 2, (int)dst & 7);
        }

        public void Neg(X64Size size, X64Register dst)
        {
            EmitByte(0xF7);
            EmitModRMByte(3, 3, (int)dst & 7);
        }

        public void Shl(X64Size size, X64Register dst, int count)
        {
            EmitByte(0xC1);
            EmitModRMByte(3, 4, (int)dst & 7);
            EmitByte((byte)count);
        }

        public void Shr(X64Size size, X64Register dst, int count)
        {
            EmitByte(0xC1);
            EmitModRMByte(3, 5, (int)dst & 7);
            EmitByte((byte)count);
        }

        public void Sar(X64Size size, X64Register dst, int count)
        {
            EmitByte(0xC1);
            EmitModRMByte(3, 7, (int)dst & 7);
            EmitByte((byte)count);
        }

        public void Shl(X64Size size, X64Register dst)
        {
            EmitByte(0xD3);
            EmitModRMByte(3, 4, (int)dst & 7);
        }

        public void Shr(X64Size size, X64Register dst)
        {
            EmitByte(0xD3);
            EmitModRMByte(3, 5, (int)dst & 7);
        }

        public void Sar(X64Size size, X64Register dst)
        {
            EmitByte(0xD3);
            EmitModRMByte(3, 7, (int)dst & 7);
        }

        public void Div(X64Size size, X64Register divisor)
        {
            EmitByte(0xF7);
            EmitModRMByte(3, 6, (int)divisor & 7);
        }

        public void Idiv(X64Size size, X64Register divisor)
        {
            EmitByte(0xF7);
            EmitModRMByte(3, 7, (int)divisor & 7);
        }

        /// <summary>MUL r/m32：EDX:EAX ← EAX × r/m32（无符号全积，64 位整型乘法用）。</summary>
        public void Mul(X64Size size, X64Register divisor)
        {
            EmitByte(0xF7);
            EmitModRMByte(3, 4, (int)divisor & 7);
        }

        public void Adc(X64Size size, X64Register dst, X64Register src) => EmitRmReg(0x11, size, dst, src);
        public void Adc(X64Size size, X64Register dst, X64MemoryOperand src) => EmitRegMem(0x13, size, dst, src);

        public void Sbb(X64Size size, X64Register dst, X64Register src) => EmitRmReg(0x19, size, dst, src);
        public void Sbb(X64Size size, X64Register dst, X64MemoryOperand src) => EmitRegMem(0x1B, size, dst, src);

        /// <summary>ADC r32, imm（0x83 /2 短形式或 0x81 /2 长形式）。</summary>
        public void AdcRegImm(X64Register dst, int imm)
        {
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

        /// <summary>SHLD r/m32, r32, CL（双精度左移：dst:src 联合左移 CL 位，dst 取高位）。</summary>
        public void ShldCl(X64Register dst, X64Register src)
        {
            EmitByte(0x0F);
            EmitByte(0xA5);
            EmitModRMByte(3, (int)src & 7, (int)dst & 7);
        }

        /// <summary>SHLD r/m32, r32, imm8。</summary>
        public void ShldImm8(X64Register dst, X64Register src, byte count)
        {
            EmitByte(0x0F);
            EmitByte(0xA4);
            EmitModRMByte(3, (int)src & 7, (int)dst & 7);
            EmitByte(count);
        }

        /// <summary>SHRD r/m32, r32, CL（双精度右移）。</summary>
        public void ShrdCl(X64Register dst, X64Register src)
        {
            EmitByte(0x0F);
            EmitByte(0xAD);
            EmitModRMByte(3, (int)src & 7, (int)dst & 7);
        }

        /// <summary>SHRD r/m32, r32, imm8。</summary>
        public void ShrdImm8(X64Register dst, X64Register src, byte count)
        {
            EmitByte(0x0F);
            EmitByte(0xAC);
            EmitModRMByte(3, (int)src & 7, (int)dst & 7);
            EmitByte(count);
        }

        /// <summary>CDQ：EDX:EAX ← 符号扩展 EAX。</summary>
        public void Cdq()
        {
            EmitByte(0x99);
        }

        /// <summary>CQO：x86 32 位模式无 64 位 RDX:RAX 符号扩展；64 位除法走 Idiv64 运行时辅助，不会调用本方法。</summary>
        public void Cqo()
        {
            throw new NotSupportedException("CQO 在 x86 32 位模式下不可用；64 位除法应使用 Idiv64 运行时辅助函数。");
        }

        public void Cvtsi2sd64(X64Register xmmDst, X64Register r64Src)
            => throw new NotSupportedException("Cvtsi2sd64 在 x86 32 位模式下不可用；long→double 转换应走 fild/fstp。");

        public void Cvttsd2si64(X64Register r64Dst, X64Register xmmSrc)
            => throw new NotSupportedException("Cvttsd2si64 在 x86 32 位模式下不可用；double→long 转换应走 fldcw/fistp。");

        // ------------------------------------------------------------------
        // x87 FPU（仅用于 long ↔ double 转换；double 运算走 SSE）
        // ------------------------------------------------------------------

        /// <summary>FILD m64int：压入 64 位有符号整数（long → double 入口）。</summary>
        public void FildM64(X64MemoryOperand src)
        {
            var memory = EncodeMemory(src);
            EmitByte(0xDF);
            EmitModRMByte(memory.Mod, 5, memory.Rm);
            EmitMemoryRest(src, memory);
        }

        /// <summary>FSTP m64：弹出栈顶并以 double 位模式存储。</summary>
        public void FstpM64(X64MemoryOperand dst)
        {
            var memory = EncodeMemory(dst);
            EmitByte(0xDD);
            EmitModRMByte(memory.Mod, 3, memory.Rm);
            EmitMemoryRest(dst, memory);
        }

        /// <summary>FISTP m64int：栈顶按当前舍入模式存为 64 位整数并出栈。</summary>
        public void FistpM64(X64MemoryOperand dst)
        {
            var memory = EncodeMemory(dst);
            EmitByte(0xDF);
            EmitModRMByte(memory.Mod, 7, memory.Rm);
            EmitMemoryRest(dst, memory);
        }

        /// <summary>FLD m64：压入 double。</summary>
        public void FldM64(X64MemoryOperand src)
        {
            var memory = EncodeMemory(src);
            EmitByte(0xDD);
            EmitModRMByte(memory.Mod, 0, memory.Rm);
            EmitMemoryRest(src, memory);
        }

        /// <summary>FSTP m32（DD /1）：弹出栈顶并以 float 位模式存储 4 字节（6e-M21 Phase 5b）。</summary>
        public void FstpM32(X64MemoryOperand dst)
        {
            var memory = EncodeMemory(dst);
            EmitByte(0xDD);
            EmitModRMByte(memory.Mod, 1, memory.Rm);
            EmitMemoryRest(dst, memory);
        }

        /// <summary>FLD m32（D9 /0）：压入 float。</summary>
        public void FldM32(X64MemoryOperand src)
        {
            var memory = EncodeMemory(src);
            EmitByte(0xD9);
            EmitModRMByte(memory.Mod, 0, memory.Rm);
            EmitMemoryRest(src, memory);
        }

        /// <summary>FILD m32int（DB /0）：压入 32 位有符号整数（6e-M21 Phase 7）。</summary>
        public void FildM32(X64MemoryOperand src)
        {
            var memory = EncodeMemory(src);
            EmitByte(0xDB);
            EmitModRMByte(memory.Mod, 0, memory.Rm);
            EmitMemoryRest(src, memory);
        }
        /// <summary>FLDCW m16：加载 x87 控制字（舍入模式切换）。</summary>
        public void FldcwM16(X64MemoryOperand src)
        {

            var memory = EncodeMemory(src);
            EmitByte(0xD9);
            EmitModRMByte(memory.Mod, 5, memory.Rm);
            EmitMemoryRest(src, memory);
        }

        /// <summary>FMULP st(1),st(0)：栈顶两元素相乘并弹出（6e-M21 Phase 7）。</summary>
        public void Fmulp()
        {
            EmitByte(0xDE);
            EmitByte(0xC9);
        }

        /// <summary>FADDP st(1),st(0)：栈顶两元素相加并弹出。</summary>
        public void Faddp()
        {
            EmitByte(0xDE);
            EmitByte(0xC1);
        }

        /// <summary>FNSTCW m16：保存 x87 控制字。</summary>
        public void FnstcwM16(X64MemoryOperand dst)
        {
            var memory = EncodeMemory(dst);
            EmitByte(0xD9);
            EmitModRMByte(memory.Mod, 7, memory.Rm);
            EmitMemoryRest(dst, memory);
        }

        public void Movzx(X64Size dstSize, X64Register dst, X64Register src)
        {
            EmitByte(0x0F);
            EmitByte(0xB6);
            EmitModRMByte(3, (int)dst & 7, (int)src & 7);
        }

        public void Movzx(X64Size dstSize, X64Register dst, X64MemoryOperand src)
        {
            var memory = EncodeMemory(src);
            EmitByte(0x0F);
            EmitByte((byte)(dstSize == X64Size.Byte ? 0xB6 : 0xB7));
            EmitModRMByte(memory.Mod, (int)dst & 7, memory.Rm);
            EmitMemoryRest(src, memory);
        }

        public void Movsxd(X64Register dst, X64Register src)
        {
            throw new NotSupportedException("MOVSXD is not supported on x86.");
        }

        public void Lea(X64Register dst, X64MemoryOperand src)
        {
            var memory = EncodeMemory(src);
            EmitByte(0x8D);
            EmitModRMByte(memory.Mod, (int)dst & 7, memory.Rm);
            EmitMemoryRest(src, memory);
        }

        public void LeaRip(X64Register dst, int symbol)
        {
            EmitByte(0x8D);
            EmitModRMByte(0, (int)dst & 7, 5);
            _dataFixups.Add((Position, symbol));
            EmitInt32(0);
        }

        public void Push(X64Register reg)
        {
            if ((int)reg >= 8)
            {
                throw new ArgumentException("x86 has only 8 general-purpose registers.", nameof(reg));
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
                throw new ArgumentException("x86 has only 8 general-purpose registers.", nameof(reg));
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
            EmitByte(0xFF);
            EmitModRMByte(3, 2, (int)reg & 7);
        }

        public void MovGs(X64Register dst, int displacement)
        {
            EmitByte(0x64); // FS segment: TEB
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
        // SSE锛坉ouble锛孖EEE-754 binary64锟?
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
        public void Ucomisd(X64Register xmmA, X64Register xmmB) => EmitSseRegReg(0x2E, 0x66, xmmA, xmmB);
        public void MovdGprToXmm(X64Register xmmDst, X64Register r32Src) => EmitSseRegReg(0x6E, 0x66, xmmDst, r32Src);
        public void MovdXmmToGpr(X64Register r32Dst, X64Register xmmSrc) => EmitSseRegReg(0x7E, 0x66, r32Dst, xmmSrc);
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
            EmitByte(0xF3);
            EmitByte(0x0F);
            EmitByte(0x10);
            EmitModRMByte(0, (int)xmmDst & 7, 5);
            _dataFixups.Add((Position, symbol));
            EmitInt32(0);
        }

        public void MovqGprToXmm(X64Register xmmDst, X64Register r64Src)
        {
            throw new NotSupportedException("MOVQ (xmm, r64) is not supported on x86.");
        }

        public void MovqXmmToGpr(X64Register r64Dst, X64Register xmmSrc)
        {
            throw new NotSupportedException("MOVQ (r64, xmm) is not supported on x86.");
        }

        public void MovsdRip(X64Register xmmDst, int symbol)
        {
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

        private void EmitSseRegReg(byte opcode, byte prefix, X64Register reg, X64Register rm)
        {
            EmitByte(prefix);
            EmitByte(0x0F);
            EmitByte(opcode);
            EmitModRMByte(3, (int)reg & 7, (int)rm & 7);
        }

        private void EmitSseRegMem(byte opcode, byte prefix, X64Register reg, X64MemoryOperand mem)
        {
            var memory = EncodeMemory(mem);
            EmitByte(prefix);
            EmitByte(0x0F);
            EmitByte(opcode);
            EmitModRMByte(memory.Mod, (int)reg & 7, memory.Rm);
            EmitMemoryRest(mem, memory);
        }

        private void EmitSseMemReg(byte opcode, byte prefix, X64MemoryOperand mem, X64Register reg)
        {
            var memory = EncodeMemory(mem);
            EmitByte(prefix);
            EmitByte(0x0F);
            EmitByte(opcode);
            EmitModRMByte(memory.Mod, (int)reg & 7, memory.Rm);
            EmitMemoryRest(mem, memory);
        }

        private void EmitSseRegImm(byte opcode, byte prefix, X64Register reg, X64Register rm, byte imm)
        {
            EmitByte(prefix);
            EmitByte(0x0F);
            EmitByte(0x3A);
            EmitByte(opcode);
            EmitModRMByte(3, (int)reg & 7, (int)rm & 7);
            EmitByte(imm);
        }

        private void EmitRegReg(byte opcode, X64Size size, X64Register reg, X64Register rm)
        {
            EmitByte(opcode);
            EmitModRMByte(3, (int)reg & 7, (int)rm & 7);
        }

        private void EmitRmReg(byte opcode, X64Size size, X64Register rm, X64Register reg)
        {
            EmitByte(opcode);
            EmitModRMByte(3, (int)reg & 7, (int)rm & 7);
        }

        private void EmitRegMem(byte opcode, X64Size size, X64Register reg, X64MemoryOperand mem)
        {
            var memory = EncodeMemory(mem);
            EmitByte(opcode);
            EmitModRMByte(memory.Mod, (int)reg & 7, memory.Rm);
            EmitMemoryRest(mem, memory);
        }

        private void EmitMemReg(byte opcode, X64Size size, X64MemoryOperand mem, X64Register reg)
        {
            var memory = EncodeMemory(mem);
            EmitByte(opcode);
            EmitModRMByte(memory.Mod, (int)reg & 7, memory.Rm);
            EmitMemoryRest(mem, memory);
        }

        private void EmitRegImm(int digit, X64Size size, X64Register dst, int imm)
        {
            if (imm is >= -128 and <= 127)
            {
                EmitByte(0x83);
                EmitModRMByte(3, digit, (int)dst & 7);
                EmitByte(unchecked((byte)(sbyte)imm));
            }
            else
            {
                EmitByte(0x81);
                EmitModRMByte(3, digit, (int)dst & 7);
                EmitInt32(imm);
            }
        }

        private (int Mod, int Rm) EncodeMemory(X64MemoryOperand mem)
        {
            var needsSib = mem.Base == X64Register.RSP || mem.Base == X64Register.R12;

            int mod;

            if (((int)mem.Base & 7) == 5 || mem.Displacement != 0)
            {
                mod = mem.Displacement is >= -128 and <= 127 ? 1 : 2;
            }
            else
            {
                mod = 0;
            }

            var rm = needsSib ? 4 : (int)mem.Base & 7;

            return (mod, rm);
        }

        private void EmitMemoryRest(X64MemoryOperand mem, (int Mod, int Rm) memory)
        {
            if (memory.Rm == 4)
            {
                EmitByte((byte)(0x04 << 3 | ((int)mem.Base & 7)));
            }

            if (memory.Mod == 1)
            {
                EmitByte(unchecked((byte)(sbyte)mem.Displacement));
            }
            else if (memory.Mod == 2)
            {
                EmitInt32(mem.Displacement);
            }
        }

        private void EmitModRMByte(int mod, int reg, int rm)
        {
            EmitByte((byte)((mod << 6) | ((reg & 7) << 3) | (rm & 7)));
        }

        private void EmitByte(byte value)
        {
            _bytes.Add(value);
        }

        private void EmitInt32(int value)
        {
            _bytes.Add((byte)value);
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)(value >> 16));
            _bytes.Add((byte)(value >> 24));
        }

        private void WriteInt32At(int offset, int value)
        {
            _bytes[offset] = (byte)value;
            _bytes[offset + 1] = (byte)(value >> 8);
            _bytes[offset + 2] = (byte)(value >> 16);
            _bytes[offset + 3] = (byte)(value >> 24);
        }
    }
}

