namespace Cocoa.CodeAnalysis.Emit.IR
{
    /// <summary>条件码（与 IAssembler 的 X64CondCode 一一对应）。</summary>
    internal enum IrCond
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
}