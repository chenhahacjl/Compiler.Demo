namespace Cocoa.CodeGen.PE
{
    public enum PeSubsystem : ushort
    {
        Unknown = 0,
        Native = 1, // IMAGE_SUBSYSTEM_NATIVE
        WindowsGui = 2, // IMAGE_SUBSYSTEM_WINDOWS_GUI
        WindowsCui = 3, // IMAGE_SUBSYSTEM_WINDOWS_CUI
    }
}
