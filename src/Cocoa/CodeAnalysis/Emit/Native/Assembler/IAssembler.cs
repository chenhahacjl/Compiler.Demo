using Cocoa.CodeAnalysis.Emit.Native.Assembler.X64;

namespace Cocoa.CodeAnalysis.Emit.Native.Assembler
{
    /// <summary>
    /// 汇编器抽象：标签/数据符号/指令编码。X64 与 X86 各有一个实现。
    /// </summary>
    internal interface IAssembler
    {
        int Position { get; }
        int DataLength { get; }

        int CreateLabel();
        void MarkLabel(int label);
        int GetLabelOffset(int label);

        int CreateDataSymbol();
        void MarkDataSymbol(int symbol);
        int GetDataOffset(int symbol);

        void WriteDataByte(byte value);
        void WriteDataInt32(int value);
        void WriteDataInt16(int value);
        void WriteDataInt64(long value);
        void WriteDataBytes(params byte[] values);
        void WriteDataBytes(System.Collections.Generic.IEnumerable<byte> values);
        void WriteDataUtf16(string value);
        void AlignData(int alignment);

        void Patch(int dataTextDelta, long imageBase);
        byte[] ToArray();
        byte[] GetData();

        void Mov(X64Size size, X64Register dst, X64Register src);
        void Mov(X64Size size, X64Register dst, X64MemoryOperand src);
        void Mov(X64Size size, X64MemoryOperand dst, X64Register src);
        void Mov(X64Size size, X64Register dst, int imm);
        void Mov(X64Size size, X64Register dst, long imm);
        void Mov(X64Register dst, long imm);
        void Mov(X64Size size, X64MemoryOperand dst, int imm);
        void MovRip(X64Size size, X64Register dst, int symbol);

        void Add(X64Size size, X64Register dst, X64Register src);
        void Add(X64Size size, X64Register dst, X64MemoryOperand src);
        void Add(X64Size size, X64MemoryOperand dst, X64Register src);
        void Add(X64Size size, X64Register dst, int imm);

        void Sub(X64Size size, X64Register dst, X64Register src);
        void Sub(X64Size size, X64Register dst, X64MemoryOperand src);
        void Sub(X64Size size, X64MemoryOperand dst, X64Register src);
        void Sub(X64Size size, X64Register dst, int imm);

        void And(X64Size size, X64Register dst, X64Register src);
        void And(X64Size size, X64Register dst, X64MemoryOperand src);
        void And(X64Size size, X64MemoryOperand dst, X64Register src);
        void And(X64Size size, X64Register dst, int imm);

        void Or(X64Size size, X64Register dst, X64Register src);
        void Or(X64Size size, X64Register dst, X64MemoryOperand src);
        void Or(X64Size size, X64MemoryOperand dst, X64Register src);
        void Or(X64Size size, X64Register dst, int imm);

        void Xor(X64Size size, X64Register dst, X64Register src);
        void Xor(X64Size size, X64Register dst, X64MemoryOperand src);
        void Xor(X64Size size, X64MemoryOperand dst, X64Register src);
        void Xor(X64Size size, X64Register dst, int imm);

        void Cmp(X64Size size, X64Register dst, X64Register src);
        void Cmp(X64Size size, X64Register dst, X64MemoryOperand src);
        void Cmp(X64Size size, X64MemoryOperand dst, X64Register src);
        void Cmp(X64Size size, X64Register dst, int imm);

        void Test(X64Size size, X64Register r1, X64Register r2);
        void Imul(X64Size size, X64Register dst, X64Register src);
        void Not(X64Size size, X64Register dst);
        void Neg(X64Size size, X64Register dst);

        void Shl(X64Size size, X64Register dst, int count);
        void Shr(X64Size size, X64Register dst, int count);
        void Sar(X64Size size, X64Register dst, int count);
        void Shl(X64Size size, X64Register dst);
        void Shr(X64Size size, X64Register dst);
        void Sar(X64Size size, X64Register dst);

        void Div(X64Size size, X64Register divisor);
        void Idiv(X64Size size, X64Register divisor);

        void Movzx(X64Size dstSize, X64Register dst, X64Register src);
        void Movzx(X64Size dstSize, X64Register dst, X64MemoryOperand src);
        void Movsxd(X64Register dst, X64Register src);

        void Lea(X64Register dst, X64MemoryOperand src);
        void LeaRip(X64Register dst, int symbol);

        void Push(X64Register reg);
        void Push(int imm);
        void Pop(X64Register reg);

        void Jmp(int label);
        void Jcc(X64CondCode cond, int label);
        void Call(int label);
        void Call(X64Register reg);

        void MovGs(X64Register dst, int displacement);
        void CallRip(int symbol);
        void Setcc(X64CondCode cond, X64Register dst);

        void Ret();
        void Nop();
    }
}
