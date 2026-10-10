using System;
using System.Collections.Generic;
using static Cocoa.CodeGen.PE.PeBinary;

namespace Cocoa.CodeGen.PE
{
    public static class PeImageBuilder
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

        }

    /// <summary>PE 镜像读取器：RVA↔文件偏移换算 + 目录解析（磁盘镜像语义）。</summary>
}
