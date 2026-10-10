using System;
using System.Buffers.Binary;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_OPTIONAL_HEADER32 — PE32 可选头（96 字节），含 BaseOfData。</summary>
    public readonly record struct ImageOptionalHeader32(
        PeOptionalMagic Magic,
        byte MajorLinkerVersion,
        byte MinorLinkerVersion,
        uint SizeOfCode,
        uint SizeOfInitializedData,
        uint SizeOfUninitializedData,
        uint AddressOfEntryPoint,
        uint BaseOfCode,
        uint BaseOfData,
        uint ImageBase,
        uint SectionAlignment,
        uint FileAlignment,
        ushort MajorOperatingSystemVersion,
        ushort MinorOperatingSystemVersion,
        ushort MajorImageVersion,
        ushort MinorImageVersion,
        ushort MajorSubsystemVersion,
        ushort MinorSubsystemVersion,
        uint Win32VersionValue,
        uint SizeOfImage,
        uint SizeOfHeaders,
        uint CheckSum,
        PeSubsystem Subsystem,
        ushort DllCharacteristics,
        uint SizeOfStackReserve,
        uint SizeOfStackCommit,
        uint SizeOfHeapReserve,
        uint SizeOfHeapCommit,
        uint LoaderFlags,
        uint NumberOfRvaAndSizes,
        ImageDataDirectory[] DataDirectories)
    {
        public static int Size => 96 + 16 * ImageDataDirectory.SizeOfEntry;

        public static ImageOptionalHeader32 Read(ReadOnlySpan<byte> s)
        {
            var directories = new ImageDataDirectory[16];
            for (var i = 0; i < directories.Length; i++)
            {
                directories[i] = ImageDataDirectory.Read(s.Slice(96 + i * ImageDataDirectory.SizeOfEntry));
            }

            return new ImageOptionalHeader32(
                (PeOptionalMagic)BinaryPrimitives.ReadUInt16LittleEndian(s),
                s[2],
                s[3],
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(12)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(16)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(20)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(24)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(28)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(32)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(36)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(40)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(42)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(44)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(46)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(48)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(50)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(52)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(56)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(60)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(64)),
                (PeSubsystem)BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(68)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(70)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(72)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(76)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(80)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(84)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(88)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(92)),
                directories);
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d, (ushort)Magic);
            d[2] = MajorLinkerVersion;
            d[3] = MinorLinkerVersion;
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), SizeOfCode);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(8), SizeOfInitializedData);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(12), SizeOfUninitializedData);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(16), AddressOfEntryPoint);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(20), BaseOfCode);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(24), BaseOfData);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(28), ImageBase);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(32), SectionAlignment);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(36), FileAlignment);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(40), MajorOperatingSystemVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(42), MinorOperatingSystemVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(44), MajorImageVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(46), MinorImageVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(48), MajorSubsystemVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(50), MinorSubsystemVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(52), Win32VersionValue);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(56), SizeOfImage);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(60), SizeOfHeaders);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(64), CheckSum);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(68), (ushort)Subsystem);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(70), DllCharacteristics);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(72), SizeOfStackReserve);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(76), SizeOfStackCommit);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(80), SizeOfHeapReserve);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(84), SizeOfHeapCommit);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(88), LoaderFlags);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(92), NumberOfRvaAndSizes);

            for (var i = 0; i < 16; i++)
            {
                DataDirectories[i].Write(d.Slice(96 + i * ImageDataDirectory.SizeOfEntry));
            }
        }
    }
}
