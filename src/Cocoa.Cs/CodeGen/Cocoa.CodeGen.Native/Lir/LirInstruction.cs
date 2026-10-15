

namespace Cocoa.CodeGen.Native.Lir
{
    /// <summary>
    /// 单条 IR 指令：三地址码（至多一个目的寄存器 + 两个操作数）。
    /// Load/Store 经 <see cref="LirMem"/> 构造，携带偏移与字节宽。
    /// <see cref="SinglePrecision"/> 标记 F* 浮点指令按 32 位单精度（SSE ss 族）发射（6e-M21 Phase 5b）。
    /// </summary>
    public sealed class LirInstruction
    {
        public LirOpCode OpCode { get; }
        public LirVirtualRegister? Dst { get; }
        public LirOperand A { get; }
        public LirOperand B { get; }
        public int Offset { get; }
        public int ByteSize { get; }
        public bool SinglePrecision { get; }

        public LirInstruction(LirOpCode opCode, LirVirtualRegister? dst, LirOperand a, LirOperand b, int offset, int byteSize, bool singlePrecision = false)
        {
            OpCode = opCode;
            Dst = dst;
            A = a;
            B = b;
            Offset = offset;
            ByteSize = byteSize;
            SinglePrecision = singlePrecision;
        }

        public LirInstruction(LirOpCode opCode, LirVirtualRegister? dst)
            : this(opCode, dst, LirOperand.None, LirOperand.None, 0, 0)
        {
        }

        public LirInstruction(LirOpCode opCode, LirVirtualRegister? dst, LirOperand a)
            : this(opCode, dst, a, LirOperand.None, 0, 0)
        {
        }

        public LirInstruction(LirOpCode opCode, LirVirtualRegister? dst, LirOperand a, LirOperand b)
            : this(opCode, dst, a, b, 0, 0)
        {
        }

        public LirInstruction(LirOpCode opCode, LirOperand a)
            : this(opCode, null, a, LirOperand.None, 0, 0)
        {
        }

        public LirInstruction(LirOpCode opCode, LirOperand a, LirOperand b)
            : this(opCode, null, a, b, 0, 0)
        {
        }

        public LirInstruction(LirOpCode opCode)
            : this(opCode, null, LirOperand.None, LirOperand.None, 0, 0)
        {
        }

        public override string ToString() => LirPrinter.Format(this);
    }
}

    