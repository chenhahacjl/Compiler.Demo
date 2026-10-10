using System;
using System.Collections.Generic;
using static Cocoa.CodeGen.PE.PeBinary;

namespace Cocoa.CodeGen.PE
{
    public sealed class PeImageReader
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
