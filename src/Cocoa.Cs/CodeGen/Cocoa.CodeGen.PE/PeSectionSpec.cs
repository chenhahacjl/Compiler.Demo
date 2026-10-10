using System;
using System.Collections.Generic;
using static Cocoa.CodeGen.PE.PeBinary;

namespace Cocoa.CodeGen.PE
{
    public sealed class PeSectionSpec
    {
        public PeSectionSpec(string name, byte[] rawData, uint virtualAddress, uint characteristics)
        {
            Name = name;
            RawData = rawData;
            VirtualAddress = virtualAddress;
            Characteristics = characteristics;
        }

        public string Name { get; }
        public byte[] RawData { get; }
        public uint VirtualAddress { get; }
        public uint Characteristics { get; }
    }
}
