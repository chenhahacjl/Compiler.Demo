namespace Cocoa.CodeGen.PE
{
    public static class PeSectionCharacteristics
    {
        public const uint TypeNoPad = 0x00000008;
        public const uint CntCode = 0x00000020; // IMAGE_SCN_CNT_CODE
        public const uint CntInitializedData = 0x00000040; // IMAGE_SCN_CNT_INITIALIZED_DATA
        public const uint CntUninitializedData = 0x00000080;
        public const uint MemPurgeable = 0x00020000;
        public const uint Align16Bytes = 0x00500000;
        public const uint Align32Bytes = 0x00600000;
        public const uint LnkNrelocOvfl = 0x01000000;
        public const uint MemDiscardable = 0x02000000;
        public const uint MemNotCached = 0x04000000;
        public const uint MemNotPaged = 0x08000000;
        public const uint MemShared = 0x10000000;
        public const uint MemExecute = 0x20000000; // IMAGE_SCN_MEM_EXECUTE
        public const uint MemRead = 0x40000000; // IMAGE_SCN_MEM_READ
        public const uint MemWrite = 0x80000000; // IMAGE_SCN_MEM_WRITE

        public const uint Text = CntCode | MemExecute | MemRead;

        public const uint Data = CntInitializedData | MemRead | MemWrite;
    }
}
