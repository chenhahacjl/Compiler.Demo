using System;
using System.Buffers.Binary;

namespace Cocoa.CodeGen.PE
{
    public enum PeUnwindOpCode : byte
    {
        PushNonvol = 0, // UWOP_PUSH_NONVOL
        AllocLarge = 1, // UWOP_ALLOC_LARGE
        AllocSmall = 2, // UWOP_ALLOC_SMALL
        SetFrame = 3, // UWOP_SET_FPREG
        SaveNonvol = 4, // UWOP_SAVE_NONVOL
        SaveNonvolFar = 5, // UWOP_SAVE_NONVOL_FAR
        SaveXmm128 = 8, // UWOP_SAVE_XMM128
        SaveXmm128Far = 9, // UWOP_SAVE_XMM128_FAR
        PushMachFrame = 10, // UWOP_PUSH_MACHFRAME
    }
}
