namespace Cocoa.CodeGen.PE
{
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
}
