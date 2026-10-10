namespace Cocoa.CodeGen.PE
{
    public enum PeMachine : ushort
    {
        Unknown = 0,
        I386 = 0x014C, // IMAGE_FILE_MACHINE_I386
        AMD64 = 0x8664, // IMAGE_FILE_MACHINE_AMD64
        ARM64 = 0xAA64, // IMAGE_FILE_MACHINE_ARM64
    }
}
