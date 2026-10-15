

namespace Cocoa.CodeGen.Native.Lir
{
    /// <summary>条件码（与 IAssembler 的 X64CondCode 一一对应）。</summary>
    public enum LirCond
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