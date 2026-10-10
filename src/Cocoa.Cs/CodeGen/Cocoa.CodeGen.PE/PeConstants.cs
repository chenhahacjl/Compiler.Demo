namespace Cocoa.CodeGen.PE
{
    public static class PeConstants
    {
        public const byte DosHeaderSize = 64;
        public const uint DosSignature = 0x5A4D; // "MZ"
        public const uint NtSignature = 0x00004550; // "PE\0\0"

        public const ulong OrdinalFlag64 = 0x8000000000000000; // IMAGE_ORDINAL_FLAG64
        public const uint OrdinalFlag32 = 0x80000000; // IMAGE_ORDINAL_FLAG32
    }
}
