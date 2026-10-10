namespace Cocoa.CodeGen.PE
{
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
}
