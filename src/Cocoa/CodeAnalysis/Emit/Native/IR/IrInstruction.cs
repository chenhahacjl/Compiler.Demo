namespace Cocoa.CodeAnalysis.Emit.Native.IR
{
    /// <summary>IR 操作数：立即数 / 虚拟寄存器 / 标签 / 数据符号 / 函数 / 运行时函数名。</summary>
    internal readonly struct IrOperand
    {
        public static readonly IrOperand None = new IrOperand(IrOperandKind.None, 0, null, null);

        public IrOperandKind Kind { get; }
        public long Imm { get; }
        public IrVirtualRegister? Register { get; }
        public object? Symbol { get; }   // 数据 key / IrFunction / 运行时函数名

        public IrOperand(IrOperandKind kind, long imm, IrVirtualRegister? reg, object? symbol)
        {
            Kind = kind;
            Imm = imm;
            Register = reg;
            Symbol = symbol;
        }

        public static IrOperand Constant(long imm) => new IrOperand(IrOperandKind.Constant, imm, null, null);
        public static IrOperand Reg(IrVirtualRegister reg) => new IrOperand(IrOperandKind.Register, 0, reg, null);
        public static IrOperand Label(int id) => new IrOperand(IrOperandKind.Label, id, null, null);
        public static IrOperand Data(string key) => new IrOperand(IrOperandKind.Data, 0, null, key);
        public static IrOperand Import(IrImport import) => new IrOperand(IrOperandKind.Import, 0, null, import);
        public static IrOperand Func(IrFunction function) => new IrOperand(IrOperandKind.Function, 0, null, function);
        public static IrOperand Runtime(string name) => new IrOperand(IrOperandKind.Runtime, 0, null, name);

        public bool IsNone => Kind == IrOperandKind.None;

        public override string ToString()
        {
            switch (Kind)
            {
                case IrOperandKind.Constant:
                    return Imm.ToString();
                case IrOperandKind.Register:
                    return Register!.ToString();
                case IrOperandKind.Label:
                    return "L" + Imm;
                case IrOperandKind.Data:
                    return "D$" + Symbol;
                case IrOperandKind.Import:
                    return "I$" + Symbol;
                case IrOperandKind.Function:
                    return ((IrFunction)Symbol!).Name;
                case IrOperandKind.Runtime:
                    return "rt$" + Symbol;
                default:
                    return "None";
            }
        }
    }

    internal enum IrOperandKind
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
    /// Load/Store 经 <see cref="IrMem"/> 构造，携带偏移与字节宽。
    /// <see cref="SinglePrecision"/> 标记 F* 浮点指令按 32 位单精度（SSE ss 族）发射（6e-M21 Phase 5b）。
    /// </summary>
    internal sealed class IrInstruction
    {
        public IrOpCode OpCode { get; }
        public IrVirtualRegister? Dst { get; }
        public IrOperand A { get; }
        public IrOperand B { get; }
        public int Offset { get; }
        public int ByteSize { get; }
        public bool SinglePrecision { get; }

        public IrInstruction(IrOpCode opCode, IrVirtualRegister? dst, IrOperand a, IrOperand b, int offset, int byteSize, bool singlePrecision = false)
        {
            OpCode = opCode;
            Dst = dst;
            A = a;
            B = b;
            Offset = offset;
            ByteSize = byteSize;
            SinglePrecision = singlePrecision;
        }

        public IrInstruction(IrOpCode opCode, IrVirtualRegister? dst)
            : this(opCode, dst, IrOperand.None, IrOperand.None, 0, 0)
        {
        }

        public IrInstruction(IrOpCode opCode, IrVirtualRegister? dst, IrOperand a)
            : this(opCode, dst, a, IrOperand.None, 0, 0)
        {
        }

        public IrInstruction(IrOpCode opCode, IrVirtualRegister? dst, IrOperand a, IrOperand b)
            : this(opCode, dst, a, b, 0, 0)
        {
        }

        public IrInstruction(IrOpCode opCode, IrOperand a)
            : this(opCode, null, a, IrOperand.None, 0, 0)
        {
        }

        public IrInstruction(IrOpCode opCode, IrOperand a, IrOperand b)
            : this(opCode, null, a, b, 0, 0)
        {
        }

        public IrInstruction(IrOpCode opCode)
            : this(opCode, null, IrOperand.None, IrOperand.None, 0, 0)
        {
        }

        public override string ToString() => IrPrinter.Format(this);
    }

    /// <summary>内存访问（Load/Store）专用工厂。</summary>
    internal static class IrMem
    {
        public static IrInstruction Load(IrVirtualRegister dst, IrVirtualRegister baseReg, int offset, int byteSize)
        {
            return new IrInstruction(IrOpCode.Load, dst, IrOperand.Reg(baseReg), IrOperand.None, offset, byteSize);
        }

        public static IrInstruction Store(IrVirtualRegister baseReg, int offset, IrVirtualRegister src, int byteSize)
        {
            return new IrInstruction(IrOpCode.Store, null, IrOperand.Reg(baseReg), IrOperand.Reg(src), offset, byteSize);
        }
    }
}