namespace Cocoa.CodeGen.Native.Lir
{
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
}
