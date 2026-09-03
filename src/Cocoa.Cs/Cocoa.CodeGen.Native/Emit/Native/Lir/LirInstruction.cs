using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Emit;

namespace Cocoa.CodeGen.Native.Lir
{
    /// <summary>IR 操作数：立即数 / 虚拟寄存器 / 标签 / 数据符号 / 函数 / 运行时函数名。</summary>
    internal readonly struct LirOperand
    {
        public static readonly LirOperand None = new LirOperand(LirOperandKind.None, 0, null, null);

        public LirOperandKind Kind { get; }
        public long Imm { get; }
        public LirVirtualRegister? Register { get; }
        public object? Symbol { get; }   // 数据 key / LirFunction / 运行时函数名

        public LirOperand(LirOperandKind kind, long imm, LirVirtualRegister? reg, object? symbol)
        {
            Kind = kind;
            Imm = imm;
            Register = reg;
            Symbol = symbol;
        }

        public static LirOperand Constant(long imm) => new LirOperand(LirOperandKind.Constant, imm, null, null);
        public static LirOperand Reg(LirVirtualRegister reg) => new LirOperand(LirOperandKind.Register, 0, reg, null);
        public static LirOperand Label(int id) => new LirOperand(LirOperandKind.Label, id, null, null);
        public static LirOperand Data(string key) => new LirOperand(LirOperandKind.Data, 0, null, key);
        public static LirOperand Import(LirImport import) => new LirOperand(LirOperandKind.Import, 0, null, import);
        public static LirOperand Func(LirFunction function) => new LirOperand(LirOperandKind.Function, 0, null, function);
        public static LirOperand Runtime(string name) => new LirOperand(LirOperandKind.Runtime, 0, null, name);

        public bool IsNone => Kind == LirOperandKind.None;

        public override string ToString()
        {
            switch (Kind)
            {
                case LirOperandKind.Constant:
                    return Imm.ToString();
                case LirOperandKind.Register:
                    return Register!.ToString();
                case LirOperandKind.Label:
                    return "L" + Imm;
                case LirOperandKind.Data:
                    return "D$" + Symbol;
                case LirOperandKind.Import:
                    return "I$" + Symbol;
                case LirOperandKind.Function:
                    return ((LirFunction)Symbol!).Name;
                case LirOperandKind.Runtime:
                    return "rt$" + Symbol;
                default:
                    return "None";
            }
        }
    }

    internal enum LirOperandKind
    {
        None,
        Constant,
        Register,
        Label,
        Data,
        Import,
        Function,
        Runtime,
    }

    /// <summary>
    /// 单条 IR 指令：三地址码（至多一个目的寄存器 + 两个操作数）。
    /// Load/Store 经 <see cref="LirMem"/> 构造，携带偏移与字节宽。
    /// <see cref="SinglePrecision"/> 标记 F* 浮点指令按 32 位单精度（SSE ss 族）发射（6e-M21 Phase 5b）。
    /// </summary>
    internal sealed class LirInstruction
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

    /// <summary>内存访问（Load/Store）专用工厂。</summary>
    internal static class LirMem
    {
        public static LirInstruction Load(LirVirtualRegister dst, LirVirtualRegister baseReg, int offset, int byteSize)
        {
            return new LirInstruction(LirOpCode.Load, dst, LirOperand.Reg(baseReg), LirOperand.None, offset, byteSize);
        }

        public static LirInstruction Store(LirVirtualRegister baseReg, int offset, LirVirtualRegister src, int byteSize)
        {
            return new LirInstruction(LirOpCode.Store, null, LirOperand.Reg(baseReg), LirOperand.Reg(src), offset, byteSize);
        }
    }
}