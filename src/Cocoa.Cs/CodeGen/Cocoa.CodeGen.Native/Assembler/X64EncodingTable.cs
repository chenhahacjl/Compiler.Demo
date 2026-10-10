using System.Collections.Generic;

namespace Cocoa.CodeGen.Native.Assembler.X64
{
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