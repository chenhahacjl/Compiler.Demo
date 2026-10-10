namespace Cocoa.CodeGen.PE
{
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
}
