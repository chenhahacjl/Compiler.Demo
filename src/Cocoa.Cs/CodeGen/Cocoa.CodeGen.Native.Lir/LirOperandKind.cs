namespace Cocoa.CodeGen.Native.Lir
{
    public enum LirOperandKind
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
}