namespace Cocoa.CodeGen.Native.Lir
{
    /// <summary>IR 操作数：立即数 / 虚拟寄存器 / 标签 / 数据符号 / 函数 / 运行时函数名。</summary>
    public readonly struct LirOperand
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
}