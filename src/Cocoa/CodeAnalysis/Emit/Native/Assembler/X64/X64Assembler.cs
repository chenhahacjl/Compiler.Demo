using System;
using System.Collections.Generic;

using Cocoa.CodeAnalysis.Emit.Native.Assembler;

namespace Cocoa.CodeAnalysis.Emit.Native.Assembler.X64
{
    internal enum X64Size
    {
        Byte = 0,
        Dword = 1,
        Qword = 2,
        Word = 3,
    }

    internal enum X64Register
    {
        RAX = 0,
        RCX = 1,
        RDX = 2,
        RBX = 3,
        RSP = 4,
        RBP = 5,
        RSI = 6,
        RDI = 7,
        R8 = 8,
        R9 = 9,
        R10 = 10,
        R11 = 11,
        R12 = 12,
        R13 = 13,
        R14 = 14,
        R15 = 15,

        EAX = 0,
        ECX = 1,
        EDX = 2,
        EBX = 3,
        ESP = 4,
        EBP = 5,
        ESI = 6,
        EDI = 7,

        XMM0 = 16,
        XMM1 = 17,
        XMM2 = 18,
        XMM3 = 19,
        XMM4 = 20,
        XMM5 = 21,
        XMM6 = 22,
        XMM7 = 23,
        XMM8 = 24,
        XMM9 = 25,
        XMM10 = 26,
        XMM11 = 27,
        XMM12 = 28,
        XMM13 = 29,
        XMM14 = 30,
        XMM15 = 31,
    }

    internal enum X64CondCode
    {
        Equal,
        NotEqual,
        Less,
        LessOrEqual,
        Greater,
        GreaterOrEqual,
        Below,
        BelowOrEqual,
        Above,
        AboveOrEqual,
        Parity,
        NoParity,
    }

    internal readonly struct X64MemoryOperand
    {
        public X64MemoryOperand(X64Register baseRegister, int displacement)
        {
            Base = baseRegister;
            Displacement = displacement;
        }

        public X64Register Base { get; }
        public int Displacement { get; }
    }

    internal sealed class X64Assembler : IAssembler
    {
        private readonly List<byte> _bytes = new List<byte>();
        private readonly List<byte> _data = new List<byte>();
        private readonly Dictionary<int, int> _labels = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _dataOffsets = new Dictionary<int, int>();
        private readonly List<(int Offset, int Label)> _labelFixups = new List<(int Offset, int Label)>();
        private readonly List<(int Offset, int Symbol)> _dataFixups = new List<(int Offset, int Symbol)>();
        private int _nextLabelId;
        private int _nextSymbolId;

        public int Position => _bytes.Count;
        public int DataPosition => _data.Count;
        public int DataLength => _data.Count;

        public int CreateLabel() => _nextLabelId++;

        public void MarkLabel(int label)
        {
            _labels.Add(label, Position);
        }

        public int GetLabelOffset(int label)
        {
            return _labels[label];
        }

        public int CreateDataSymbol() => _nextSymbolId++;

        public void MarkDataSymbol(int symbol)
        {
            _dataOffsets.Add(symbol, DataPosition);
        }

        public int GetDataOffset(int symbol)
        {
            return _dataOffsets[symbol];
        }

        public void WriteDataByte(byte value)
        {
            _data.Add(value);
        }

        public void WriteDataInt32(int value)
        {
            _data.Add((byte)value);
            _data.Add((byte)(value >> 8));
            _data.Add((byte)(value >> 16));
            _data.Add((byte)(value >> 24));
        }

        public void WriteDataInt16(int value)
        {
            _data.Add((byte)value);
            _data.Add((byte)(value >> 8));
        }

        public void WriteDataInt64(long value)
        {
            _data.Add((byte)value);
            _data.Add((byte)(value >> 8));
            _data.Add((byte)(value >> 16));
            _data.Add((byte)(value >> 24));
            _data.Add((byte)(value >> 32));
            _data.Add((byte)(value >> 40));
            _data.Add((byte)(value >> 48));
            _data.Add((byte)(value >> 56));
        }

        public void WriteDataBytes(params byte[] values)
        {
            _data.AddRange(values);
        }

        public void WriteDataBytes(IEnumerable<byte> values)
        {
            _data.AddRange(values);
        }

        public void WriteDataUtf16(string value)
        {
            WriteDataInt32(value.Length);
            foreach (var c in value)
            {
                WriteDataInt16(c);
            }
        }

        public void AlignData(int alignment)
        {
            while (_data.Count % alignment != 0)
            {
                _data.Add(0);
            }
        }

        public void Patch(int dataTextDelta, long imageBase)
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

                WriteInt32At(fixup.Offset, dataTextDelta + dataOffset - (fixup.Offset + 4));
            }
        }

        public byte[] ToArray()
        {
            return _bytes.ToArray();
        }

        public byte[] GetData()
        {
            return _data.ToArray();
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
        public void Cvtsi2sd(X64Register xmmDst, X64Register r32Src) => EmitSseRegReg(0x2A, 0xF2, xmmDst, r32Src);
        public void Cvttsd2si(X64Register r32Dst, X64Register xmmSrc) => EmitSseRegReg(0x2C, 0xF2, r32Dst, xmmSrc);
        public void Ucomisd(X64Register xmmA, X64Register xmmB) => EmitSseRegReg(0x2E, 0x66, xmmA, xmmB);
        public void MovdGprToXmm(X64Register xmmDst, X64Register r32Src) => EmitSseRegReg(0x6E, 0x66, xmmDst, r32Src);
        public void MovdXmmToGpr(X64Register r32Dst, X64Register xmmSrc) => EmitSseRegReg(0x7E, 0x66, xmmSrc, r32Dst);
        public void MovqGprToXmm(X64Register xmmDst, X64Register r64Src) => EmitSseRegReg(0x7E, 0xF3, xmmDst, r64Src, rexW: true);
        public void MovqXmmToGpr(X64Register r64Dst, X64Register xmmSrc) => EmitSseRegReg(0xD6, 0x66, xmmSrc, r64Dst, rexW: true);
        public void Pinsrd(X64Register xmmDst, X64Register r32Src, byte imm) => EmitSseRegImm(0x22, 0x66, xmmDst, r32Src, imm);
        public void Pextrd(X64Register r32Dst, X64Register xmmSrc, byte imm) => EmitSseRegImm(0x16, 0x66, r32Dst, xmmSrc, imm);

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

        private void EmitSseRegReg(byte opcode, byte prefix, X64Register reg, X64Register rm, bool rexW = false)
        {
            EmitByte(prefix);
            EmitRex(0x40 | (rexW ? 0x08 : 0) | (((int)reg & 8) != 0 ? 0x04 : 0) | (((int)rm & 8) != 0 ? 0x01 : 0));
            EmitByte(0x0F);
            EmitByte(opcode);
            EmitModRMByte(3, (int)reg & 7, (int)rm & 7);
        }

        private void EmitSseRegMem(byte opcode, byte prefix, X64Register reg, X64MemoryOperand mem)
        {
            var memory = EncodeMemory(mem);
            EmitByte(prefix);
            EmitRex(0x40 | (((int)reg & 8) != 0 ? 0x04 : 0) | memory.RexB);
            EmitByte(0x0F);
            EmitByte(opcode);
            EmitModRMByte(memory.Mod, (int)reg & 7, memory.Rm);
            EmitMemoryRest(mem, memory);
        }

        private void EmitSseMemReg(byte opcode, byte prefix, X64MemoryOperand mem, X64Register reg)
        {
            var memory = EncodeMemory(mem);
            EmitByte(prefix);
            EmitRex(0x40 | (((int)reg & 8) != 0 ? 0x04 : 0) | memory.RexB);
            EmitByte(0x0F);
            EmitByte(opcode);
            EmitModRMByte(memory.Mod, (int)reg & 7, memory.Rm);
            EmitMemoryRest(mem, memory);
        }

        private void EmitSseRegImm(byte opcode, byte prefix, X64Register reg, X64Register rm, byte imm)
        {
            EmitByte(prefix);
            EmitRex(0x40 | (((int)reg & 8) != 0 ? 0x04 : 0) | (((int)rm & 8) != 0 ? 0x01 : 0));
            EmitByte(0x0F);
            EmitByte(0x3A);
            EmitByte(opcode);
            EmitModRMByte(3, (int)reg & 7, (int)rm & 7);
            EmitByte(imm);
        }

        private void EmitRegReg(byte opcode, X64Size size, X64Register reg, X64Register rm)
        {
            EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)reg >= 8 ? 0x04 : 0) | ((int)rm >= 8 ? 0x01 : 0));
            EmitByte(opcode);
            EmitModRMByte(3, (int)reg & 7, (int)rm & 7);
        }

        private void EmitRmReg(byte opcode, X64Size size, X64Register rm, X64Register reg)
        {
            EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)reg >= 8 ? 0x04 : 0) | ((int)rm >= 8 ? 0x01 : 0));
            EmitByte(opcode);
            EmitModRMByte(3, (int)reg & 7, (int)rm & 7);
        }

        private void EmitRegMem(byte opcode, X64Size size, X64Register reg, X64MemoryOperand mem)
        {
            var memory = EncodeMemory(mem);
            EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)reg >= 8 ? 0x04 : 0) | memory.RexB);
            EmitByte(opcode);
            EmitModRMByte(memory.Mod, (int)reg & 7, memory.Rm);
            EmitMemoryRest(mem, memory);
        }

        private void EmitMemReg(byte opcode, X64Size size, X64MemoryOperand mem, X64Register reg)
        {
            var memory = EncodeMemory(mem);
            EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)reg >= 8 ? 0x04 : 0) | memory.RexB);
            EmitByte(opcode);
            EmitModRMByte(memory.Mod, (int)reg & 7, memory.Rm);
            EmitMemoryRest(mem, memory);
        }

        private void EmitRegImm(int digit, X64Size size, X64Register dst, int imm)
        {
            if (imm is >= -128 and <= 127)
            {
                EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)dst >= 8 ? 0x01 : 0));
                EmitByte(0x83);
                EmitModRMByte(3, digit, (int)dst & 7);
                EmitByte(unchecked((byte)(sbyte)imm));
            }
            else
            {
                EmitRex(0x40 | (size == X64Size.Qword ? 0x08 : 0) | ((int)dst >= 8 ? 0x01 : 0));
                EmitByte(0x81);
                EmitModRMByte(3, digit, (int)dst & 7);
                EmitInt32(imm);
            }
        }

        private (int Mod, int Rm, int RexB) EncodeMemory(X64MemoryOperand mem)
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
            var rexB = (int)mem.Base >= 8 ? 0x01 : 0;

            return (mod, rm, rexB);
        }

        private void EmitMemoryRest(X64MemoryOperand mem, (int Mod, int Rm, int RexB) memory)
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

        private void EmitRex(int rex)
        {
            if (rex != 0x40)
            {
                EmitByte((byte)rex);
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

        private void EmitInt64(long value)
        {
            _bytes.Add((byte)value);
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)(value >> 16));
            _bytes.Add((byte)(value >> 24));
            _bytes.Add((byte)(value >> 32));
            _bytes.Add((byte)(value >> 40));
            _bytes.Add((byte)(value >> 48));
            _bytes.Add((byte)(value >> 56));
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
