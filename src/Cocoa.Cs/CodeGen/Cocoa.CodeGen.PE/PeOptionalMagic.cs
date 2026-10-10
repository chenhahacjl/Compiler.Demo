namespace Cocoa.CodeGen.PE
{
    public enum PeOptionalMagic : ushort
    {
        Pe32 = 0x10B, // IMAGE_NT_OPTIONAL_HDR32_MAGIC
        Pe32Plus = 0x20B, // IMAGE_NT_OPTIONAL_HDR64_MAGIC
    }
}
