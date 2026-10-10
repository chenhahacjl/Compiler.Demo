namespace Cocoa.CodeGen.PE
{
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
}
