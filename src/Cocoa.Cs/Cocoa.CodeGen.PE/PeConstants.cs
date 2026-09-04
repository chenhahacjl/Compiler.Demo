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

    public enum PeOptionalMagic : ushort
    {
        Pe32 = 0x10B, // IMAGE_NT_OPTIONAL_HDR32_MAGIC
        Pe32Plus = 0x20B, // IMAGE_NT_OPTIONAL_HDR64_MAGIC
    }

    public enum PeMachine : ushort
    {
        Unknown = 0,
        I386 = 0x014C, // IMAGE_FILE_MACHINE_I386
        AMD64 = 0x8664, // IMAGE_FILE_MACHINE_AMD64
        ARM64 = 0xAA64, // IMAGE_FILE_MACHINE_ARM64
    }

    public enum PeSubsystem : ushort
    {
        Unknown = 0,
        Native = 1, // IMAGE_SUBSYSTEM_NATIVE
        WindowsGui = 2, // IMAGE_SUBSYSTEM_WINDOWS_GUI
        WindowsCui = 3, // IMAGE_SUBSYSTEM_WINDOWS_CUI
    }

    public enum PeDataDirectoryEntry
    {
        Export = 0,
        Import = 1,
        Resource = 2,
        Exception = 3,
        Security = 4,
        BaseReloc = 5,
        Debug = 6,
        Architecture = 7,
        GlobalPtr = 8,
        Tls = 9,
        LoadConfig = 10,
        BoundImport = 11,
        Iat = 12,
        DelayImport = 13,
        ComDescriptor = 14,
        Reserved = 15,
    }

    public static class PeFileCharacteristics
    {
        public const ushort RelocsStripped = 0x0001;
        public const ushort ExecutableImage = 0x0002; // IMAGE_FILE_EXECUTABLE_IMAGE
        public const ushort LineNumsStripped = 0x0004;
        public const ushort LocalSymsStripped = 0x0008;
        public const ushort AggressiveWsTrim = 0x0010;
        public const ushort LargeAddressAware = 0x0020; // IMAGE_FILE_LARGE_ADDRESS_AWARE
        public const ushort BytesReversedLo = 0x0080;
        public const ushort Machine32Bit = 0x0100;
        public const ushort DebugStripped = 0x0200;
        public const ushort RemovableRunFromSwap = 0x0400;
        public const ushort NetRunFromSwap = 0x0800;
        public const ushort System = 0x1000;
        public const ushort Dll = 0x2000; // IMAGE_FILE_DLL
        public const ushort UpSystemOnly = 0x4000;
        public const ushort BytesReversedHi = 0x8000;

        public const ushort CurrentImage = ExecutableImage | LargeAddressAware;
    }

    public static class PeDllCharacteristics
    {
        public const ushort HighEntropyVA = 0x0020; // IMAGE_DLLCHARACTERISTICS_HIGH_ENTROPY_VA
        public const ushort DynamicBase = 0x0040; // IMAGE_DLLCHARACTERISTICS_DYNAMIC_BASE
        public const ushort ForceIntegrity = 0x0080;
        public const ushort NxChipCompat = 0x0100; // IMAGE_DLLCHARACTERISTICS_NX_COMPAT
        public const ushort NoIsolation = 0x0200;
        public const ushort NoSeh = 0x0400;
        public const ushort NoBind = 0x0800;
        public const ushort AppContainer = 0x1000;
        public const ushort WdmDriver = 0x2000;
        public const ushort GuardCf = 0x4000; // IMAGE_DLLCHARACTERISTICS_GUARD_CF
        public const ushort TerminalServerAware = 0x8000;

        public const ushort CurrentImage = HighEntropyVA | DynamicBase | NxChipCompat;
    }

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

    public enum PeRelocType : byte
    {
        Absolute = 0, // IMAGE_REL_BASED_ABSOLUTE
        High = 1,
        Low = 2,
        HighLow = 3,
        HighAdj = 4,
        MachineDependent = 5,
        Reserved = 6,
        MachineDependentAlt = 7,
        Dir64 = 10, // IMAGE_REL_BASED_DIR64
    }

    public enum PeDebugType : uint
    {
        Unknown = 0, // IMAGE_DEBUG_TYPE_UNKNOWN
        Coff = 1,
        CodeView = 2, // IMAGE_DEBUG_TYPE_CODEVIEW
        Fpo = 3,
        Misc = 4,
        Exception = 5,
        Fixup = 6,
        OmapToSrc = 7,
        OmapFromSrc = 8,
        Borland = 9,
        Reserved10 = 10,
        Clsid = 11,
    }
}