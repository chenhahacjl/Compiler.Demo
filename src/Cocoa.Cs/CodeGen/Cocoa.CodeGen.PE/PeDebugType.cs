namespace Cocoa.CodeGen.PE
{
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
