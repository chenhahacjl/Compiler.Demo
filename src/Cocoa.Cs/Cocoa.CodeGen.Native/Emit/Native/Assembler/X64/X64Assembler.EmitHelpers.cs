using System;
using System.Collections.Generic;

using Cocoa.CodeGen.Native.Assembler;
using Cocoa.CodeGen.PE;

using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit;

namespace Cocoa.CodeGen.Native.Assembler.X64
{

    internal sealed partial class X64Assembler : IAssembler
    {
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
