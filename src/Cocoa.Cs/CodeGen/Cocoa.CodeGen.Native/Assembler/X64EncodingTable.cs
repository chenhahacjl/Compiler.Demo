using System.Collections.Generic;

namespace Cocoa.CodeGen.Native.Assembler.X64
{
    /// <summary>
    /// Rm 整型指令编码条目。对照源：
    ///   · Intel SDM Vol.2（MOV 0x88/0x8A-0x8B/0x89/0xC6/0xC7；ALU 0x00-0x07 组）
    ///   · LLVM X86InstrArithmetic.td 的 BinOp 模板族（按 mnemonic 提供 r/m,r 与 r,r/m 两组 opcode：
    ///     ADD64rr=0x01、ADD64rm=0x03；MOV64rm=0x8B、MOV64mr=0x89，元数据见 X86InstrFormats.td/X86InstrInfo.td）。
    ///  方向语义（与面向调用方的公共方法对齐）：
    ///     OpR = r, r/m 方向（目标寄存器，源 rm）：MOV 0x8B、ADD 0x03——供 EmitRegReg/EmitRegMem（MOV reg,reg / ALU reg,mem）
    ///     OpM = r/m, r 方向（目标 r/m，源 reg）：MOV 0x89、ADD 0x01——供 EmitMemReg（mem,reg）与 EmitRmReg（ALU reg,reg，目标当 r/m）
    ///   字节由 X64AssemblerTests / X64AssemblerEncodingTests / X64EncodingMatrixTests（表项对拍 + 模板断言矩阵）锁定；
    ///   兼容性：reg-reg 用 OpM 与 LLVM 的 rm 目标约定字节一致（add eax, ecx = 01 C8）。
    /// </summary>
    public readonly struct IntRmEncoding
    {
        public IntRmEncoding(string name, byte opR, byte opM, byte digit, bool isMove = false)
        {
            Name = name;
            OpR = opR;   // r, r/m（reg 作 ModRM.reg）：MOV 0x8B；ADD 0x03；OR 0x0B；ADC 0x13；SBB 0x1B；AND 0x23；SUB 0x2B；XOR 0x33；CMP 0x3B
            OpM = opM;   // r/m, r（reg 作 ModRM.reg，源，rm 为目标）：MOV 0x89；ADD 0x01；OR 0x09；ADC 0x11；SBB 0x19；AND 0x21；SUB 0x29；XOR 0x31；CMP 0x39
            Digit = digit; // 立即数 opcode 组扩展（0x80/0x81/0x83 ModRM.reg）：ADD=0 OR=1 ADC=2 AND=4 SUB=5 XOR=6 CMP=7；MOV 用 C7/0 与 B8+r 专属
            DigitOctad = (byte)(0x80 + digit); // 0x80 立即数基（8-bit 与 32-bit 共享 Digit 组）
            IsMove = isMove;
        }

        public string Name { get; }
        public byte OpR { get; }
        public byte OpM { get; }
        public byte Digit { get; }
        public byte DigitOctad { get; }
        public bool IsMove { get; }

        public override string ToString() => $"{Name} r:0x{OpR:X2} m:0x{OpM:X2} /digit:{Digit}";
    }

    /// <summary>整数 Rm 指令编码表（P1：手写 opcode 收敛为数据，供发射 + 断言矩阵 + LLVM 对照）。</summary>
    public static class X64EncodingTable
    {
        public static readonly IntRmEncoding Mov = new("MOV", 0x8B, 0x89, 0, isMove: true);
        public static readonly IntRmEncoding Add = new("ADD", 0x03, 0x01, 0);
        public static readonly IntRmEncoding Or = new("OR", 0x0B, 0x09, 1);
        public static readonly IntRmEncoding Adc = new("ADC", 0x13, 0x11, 2);
        public static readonly IntRmEncoding Sbb = new("SBB", 0x1B, 0x19, 3);
        public static readonly IntRmEncoding And = new("AND", 0x23, 0x21, 4);
        public static readonly IntRmEncoding Sub = new("SUB", 0x2B, 0x29, 5);
        public static readonly IntRmEncoding Xor = new("XOR", 0x33, 0x31, 6);
        public static readonly IntRmEncoding Cmp = new("CMP", 0x3B, 0x39, 7);

        /// <summary>全部条目（含 MOV）——断言矩阵遍历源。</summary>
        public static IReadOnlyList<IntRmEncoding> All { get; } = new[] { Mov, Add, Or, Adc, Sbb, And, Sub, Xor, Cmp };

        /// <summary>ALU 算子条目（含立即数 digit 语义；MOV 特殊处理）。</summary>
        public static IReadOnlyList<IntRmEncoding> Alu { get; } = new[] { Add, Or, Adc, Sbb, And, Sub, Xor, Cmp };
    }
}