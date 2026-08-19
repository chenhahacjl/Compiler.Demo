using System;
using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Emit.Native.PEFile
{
    /// <summary>节规格：虚拟地址/大小由 Builder 决定时传 0 自动布局，否则显式指定。</summary>
    internal sealed class PeSectionSpec
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

    internal sealed class PeImageConfig
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
        public uint SizeOfHeaders { get; init; } = 0x400;
        public ushort MajorOperatingSystemVersion { get; init; } = 6;
        public ushort MinorOperatingSystemVersion { get; init; } = 0;
        public ushort MajorSubsystemVersion { get; init; } = 6;
        public ushort MinorSubsystemVersion { get; init; } = 0;
        public ushort FileCharacteristicsOverride { get; init; } = 0;
    }

    /// <summary>PE 镜像组装器：DOS 头 + PE 签名 + COFF + 可选头 + 节表 + 各节 raw。</summary>
    internal static class PeImageBuilder
    {
        /// <summary>DOS stub 长度：位于 DOS 头（0x40）与 PE 签名之间。</summary>
        public const int DosStubSize = 0x40;

        public static byte[] Build(PeImageConfig config, IReadOnlyList<PeSectionSpec> sections, IReadOnlyList<(PeDataDirectoryEntry Entry, uint Rva, uint Size)> directories, ushort additionalFileCharacteristics = 0)
        {
            var headers = BuildHeaders(config, sections, directories, additionalFileCharacteristics);
            var sizeOfHeaders = (int)config.SizeOfHeaders;

            var rawSizes = new int[sections.Count];
            var rawOffsets = new int[sections.Count];
            var rawOffset = sizeOfHeaders;
            for (var i = 0; i < sections.Count; i++)
            {
                var rawSize = Align(sections[i].RawData.Length, (int)config.FileAlignment);
                rawSizes[i] = rawSize;
                rawOffsets[i] = rawOffset;
                rawOffset += rawSize;
            }

            var image = new byte[rawOffset];
            headers.CopyTo(image, 0);

            for (var i = 0; i < sections.Count; i++)
            {
                sections[i].RawData.CopyTo(image, rawOffsets[i]);
            }

            return image;
        }

        private static byte[] BuildHeaders(PeImageConfig config, IReadOnlyList<PeSectionSpec> sections, IReadOnlyList<(PeDataDirectoryEntry Entry, uint Rva, uint Size)> directories, ushort additionalFileCharacteristics)
        {
            var sectionTableOffset = PeConstants.DosHeaderSize + DosStubSize + 4 + ImageFileHeader.Size;
            var pe32 = config.Machine == PeMachine.I386;
            var optionalHeaderSize = pe32 ? ImageOptionalHeader32.Size : ImageOptionalHeader64.Size;

            var headers = new byte[config.SizeOfHeaders];

            var dos = new ImageDosHeader(
                0x5A4D, 0x90, 1, 0, 4, 0, 0xFFFF, 0, 0xB8, 0, 0, 0, 0x40, 0,
                new byte[8], 0, 0, new byte[20], 0x80);
            dos.Write(headers);

            WriteDosStub(headers);

            var sizeOfCode = 0u;
            var sizeOfInitializedData = 0u;
            uint lastSectionEnd = 0;
            uint baseOfCode = 0;
            uint baseOfData = 0;
            for (var i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                var virtualEnd = section.VirtualAddress + (uint)section.RawData.Length;
                if (virtualEnd > lastSectionEnd)
                {
                    lastSectionEnd = virtualEnd;
                }

                if ((section.Characteristics & PeSectionCharacteristics.CntCode) != 0)
                {
                    if (baseOfCode == 0) baseOfCode = section.VirtualAddress;
                    sizeOfCode += Align((uint)section.RawData.Length, config.FileAlignment);
                }
                else if ((section.Characteristics & PeSectionCharacteristics.CntInitializedData) != 0)
                {
                    if (baseOfData == 0) baseOfData = section.VirtualAddress;
                    sizeOfInitializedData += Align((uint)section.RawData.Length, config.FileAlignment);
                }
            }

            if (pe32)
            {
                var optionalHeader32 = new ImageOptionalHeader32(
                    PeOptionalMagic.Pe32,
                    9, 0,
                    sizeOfCode,
                    sizeOfInitializedData,
                    0,
                    config.AddressOfEntryPoint,
                    baseOfCode,
                    baseOfData,
                    (uint)config.ImageBase,
                    config.SectionAlignment,
                    config.FileAlignment,
                    config.MajorOperatingSystemVersion,
                    config.MinorOperatingSystemVersion,
                    0, 0,
                    config.MajorSubsystemVersion,
                    config.MinorSubsystemVersion,
                    0,
                    Align(lastSectionEnd, config.SectionAlignment),
                    config.SizeOfHeaders,
                    0,
                    (PeSubsystem)config.Subsystem,
                    config.DllCharacteristics,
                    0x100000,
                    0x20000,
                    0x100000,
                    0x1000,
                    0,
                    16,
                    ReadDirectories(directories));
                optionalHeader32.Write(headers.AsSpan(sectionTableOffset));
            }
            else
            {
                var optionalHeader = new ImageOptionalHeader64(
                    PeOptionalMagic.Pe32Plus,
                    9, 0,
                    sizeOfCode,
                    sizeOfInitializedData,
                    0,
                    config.AddressOfEntryPoint,
                    0x1000,
                    config.ImageBase,
                    config.SectionAlignment,
                    config.FileAlignment,
                    config.MajorOperatingSystemVersion,
                    config.MinorOperatingSystemVersion,
                    0, 0,
                    config.MajorSubsystemVersion,
                    config.MinorSubsystemVersion,
                    0,
                    Align(lastSectionEnd, config.SectionAlignment),
                    config.SizeOfHeaders,
                    0,
                    (PeSubsystem)config.Subsystem,
                    config.DllCharacteristics,
                    0x100000,
                    0x20000,
                    0x100000,
                    0x1000,
                    0,
                    16,
                    ReadDirectories(directories));
                optionalHeader.Write(headers.AsSpan(sectionTableOffset));
            }

            var fileHeader = new ImageFileHeader(
                (ushort)config.Machine,
                (ushort)sections.Count,
                0, 0, 0,
                (ushort)optionalHeaderSize,
                config.FileCharacteristicsOverride != 0
                    ? config.FileCharacteristicsOverride
                    : (ushort)(PeFileCharacteristics.CurrentImage | additionalFileCharacteristics));
            fileHeader.Write(headers.AsSpan(PeConstants.DosHeaderSize + DosStubSize + 4));

            headers[PeConstants.DosHeaderSize + DosStubSize] = 0x50; // 'P'
            headers[PeConstants.DosHeaderSize + DosStubSize + 1] = 0x45; // 'E'
            headers[PeConstants.DosHeaderSize + DosStubSize + 2] = 0;
            headers[PeConstants.DosHeaderSize + DosStubSize + 3] = 0;

            for (var i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                var rawSize = Align((uint)section.RawData.Length, config.FileAlignment);
                var rawOffset = (uint)config.SizeOfHeaders;
                for (var j = 0; j < i; j++)
                {
                    rawOffset += Align((uint)sections[j].RawData.Length, config.FileAlignment);
                }

                var name = new byte[8];
                for (var k = 0; k < section.Name.Length && k < 8; k++)
                {
                    name[k] = (byte)section.Name[k];
                }

                var sectionHeader = new ImageSectionHeader(
                    name,
                    (uint)section.RawData.Length,
                    section.VirtualAddress,
                    rawSize,
                    rawOffset,
                    0, 0, 0, 0,
                    section.Characteristics);
                sectionHeader.Write(headers.AsSpan(sectionTableOffset + optionalHeaderSize + i * ImageSectionHeader.Size));
            }

            return headers;
        }

        private static void WriteDosStub(Span<byte> headers)
        {
            // 标准 MSVC DOS stub："This program cannot be run in DOS mode."
            var stub = new byte[]
            {
                0x0E, 0x1F,             // push cs; pop ds
                0xBA, 0x0E, 0x00,       // mov dx, 0x0E
                0xB4, 0x09,             // mov ah, 9
                0xCD, 0x21,             // int 0x21
                0xB8, 0x01, 0x4C,       // mov ax, 0x4C01
                0xCD, 0x21,             // int 0x21
                0x54, 0x68, 0x69, 0x73, 0x20, 0x70, 0x72, 0x6F, 0x67, 0x72, 0x61, 0x6D,
                0x20, 0x63, 0x61, 0x6E, 0x6E, 0x6F, 0x74, 0x20, 0x62, 0x65, 0x20, 0x72,
                0x75, 0x6E, 0x20, 0x69, 0x6E, 0x20, 0x44, 0x4F, 0x53, 0x20, 0x6D, 0x6F,
                0x64, 0x65, 0x2E, 0x0D, 0x0D, 0x0A, 0x24, // "This program cannot be run in DOS mode.\r\r\n$"
            };
            stub.CopyTo(headers.Slice(PeConstants.DosHeaderSize));
        }

        private static ImageDataDirectory[] ReadDirectories(IReadOnlyList<(PeDataDirectoryEntry Entry, uint Rva, uint Size)> directories)
        {
            var result = new ImageDataDirectory[16];
            foreach (var (entry, rva, size) in directories)
            {
                result[(int)entry] = new ImageDataDirectory(rva, size);
            }

            return result;
        }

        private static uint Align(uint value, uint alignment)
        {
            var remainder = value % alignment;
            return remainder == 0 ? value : value + alignment - remainder;
        }

        private static int Align(int value, int alignment)
        {
            var remainder = value % alignment;
            return remainder == 0 ? value : value + alignment - remainder;
        }
    }

    /// <summary>PE 镜像读取器：RVA↔文件偏移换算 + 目录解析（磁盘镜像语义）。</summary>
    internal sealed class PeImageReader
    {
        private readonly byte[] _image;
        private readonly bool _isPe32Plus;
        private readonly ImageDataDirectory[] _directories;
        private readonly uint _sizeOfHeaders;
        private readonly List<(uint Rva, uint Size, uint PointerToRawData)> _sections = new();

        private PeImageReader(byte[] image, bool isPe32Plus, ImageDataDirectory[] directories, uint sizeOfHeaders)
        {
            _image = image;
            _isPe32Plus = isPe32Plus;
            _directories = directories;
            _sizeOfHeaders = sizeOfHeaders;
        }

        public bool IsPe32Plus => _isPe32Plus;

        public ulong ImageBase { get; private set; }

        public uint EntryPointRva { get; private set; }

        public static PeImageReader? TryOpen(ReadOnlySpan<byte> image)
        {
            if (image.Length < PeConstants.DosHeaderSize + 4)
            {
                return null;
            }

            var dos = ImageDosHeader.Read(image.Slice(0, PeConstants.DosHeaderSize));
            if (dos.EMagic != PeConstants.DosSignature || dos.ELfanew <= 0 || dos.ELfanew + ImageNtHeaders64.Size > image.Length)
            {
                return null;
            }

            var ntOffset = dos.ELfanew;
            var signature = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(ntOffset, 4));
            if (signature != PeConstants.NtSignature)
            {
                return null;
            }

            var fileHeader = ImageFileHeader.Read(image.Slice(ntOffset + 4));
            var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(ntOffset + 24));

            var directories = new ImageDataDirectory[16];
            ulong imageBase = 0;
            uint entryPoint = 0;
            uint sizeOfHeaders = 0;

            var isPe32Plus = magic == (ushort)PeOptionalMagic.Pe32Plus;

            if (isPe32Plus)
            {
                var optional = ImageOptionalHeader64.Read(image.Slice(ntOffset + 24));
                imageBase = optional.ImageBase;
                entryPoint = optional.AddressOfEntryPoint;
                sizeOfHeaders = optional.SizeOfHeaders;
                Array.Copy(optional.DataDirectories, directories, directories.Length);
            }
            else
            {
                var optional = ImageOptionalHeader32.Read(image.Slice(ntOffset + 24));
                imageBase = optional.ImageBase;
                entryPoint = optional.AddressOfEntryPoint;
                sizeOfHeaders = optional.SizeOfHeaders;
                Array.Copy(optional.DataDirectories, directories, directories.Length);
            }

            var reader = new PeImageReader(image.ToArray(), isPe32Plus, directories, sizeOfHeaders);
            reader.ImageBase = imageBase;
            reader.EntryPointRva = entryPoint;

            var sectionTableOffset = ntOffset + 24 + (isPe32Plus ? ImageOptionalHeader64.Size : ImageOptionalHeader32.Size);
            for (var i = 0; i < fileHeader.NumberOfSections; i++)
            {
                var header = ImageSectionHeader.Read(image.Slice(sectionTableOffset + i * ImageSectionHeader.Size));
                reader._sections.Add((header.VirtualAddress, header.VirtualSize, header.PointerToRawData));
            }

            return reader;
        }

        public ImageDataDirectory GetDirectory(PeDataDirectoryEntry entry)
        {
            return _directories[(int)entry];
        }

        public uint RvaToFileOffset(uint rva)
        {
            if (rva < _sizeOfHeaders || _sections.Count == 0)
            {
                return rva;
            }

            foreach (var (sectionRva, size, raw) in _sections)
            {
                if (rva >= sectionRva && rva < sectionRva + Math.Max(size, 1))
                {
                    return raw + (rva - sectionRva);
                }
            }

            return rva;
        }

        public uint FileOffsetToRva(uint fileOffset)
        {
            foreach (var section in _sections)
            {
                if (fileOffset >= section.PointerToRawData && fileOffset < section.PointerToRawData + Math.Max(section.Size, 1))
                {
                    return section.Rva + (fileOffset - section.PointerToRawData);
                }
            }

            return fileOffset;
        }

        public ReadOnlySpan<byte> Image => _image;
    }
}