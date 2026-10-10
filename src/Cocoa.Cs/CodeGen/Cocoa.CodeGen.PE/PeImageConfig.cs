using System;
using System.Collections.Generic;
using static Cocoa.CodeGen.PE.PeBinary;

namespace Cocoa.CodeGen.PE
{
    public sealed class PeImageConfig
    {
        public PeImageConfig(PeMachine machine, ulong imageBase, ushort subsystem, ushort dllCharacteristics, uint addressOfEntryPoint)
        {
            Machine = machine;
            ImageBase = imageBase;
            Subsystem = subsystem;
            DllCharacteristics = dllCharacteristics;
            AddressOfEntryPoint = addressOfEntryPoint;
        }

        public PeMachine Machine { get; }
        public ulong ImageBase { get; }
        public ushort Subsystem { get; }
        public ushort DllCharacteristics { get; }
        public uint AddressOfEntryPoint { get; }

        public uint SectionAlignment { get; init; } = 0x1000;
        public uint FileAlignment { get; init; } = 0x200;
        public uint SizeOfHeaders { get; init; } = 0x1000;
        public ushort MajorOperatingSystemVersion { get; init; } = 6;
        public ushort MinorOperatingSystemVersion { get; init; } = 0;
        public ushort MajorSubsystemVersion { get; init; } = 6;
        public ushort MinorSubsystemVersion { get; init; } = 0;
        public ushort FileCharacteristicsOverride { get; init; } = 0;
    }

}
