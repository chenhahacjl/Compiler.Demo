using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Cocoa.CodeGen.PE
{
    /// <summary>TypeOffset 解码：类型与偏移以 WORD 位域存储。</summary>
    public readonly record struct PeRelocationEntry(PeRelocType Type, int Offset)
    {
        public static PeRelocationEntry FromWord(ushort value)
        {
            return new PeRelocationEntry((PeRelocType)(value >> 12), value & 0x0FFF);
        }

        public ushort ToWord()
        {
            return (ushort)(((int)Type << 12) | (Offset & 0x0FFF));
        }
    }
}
