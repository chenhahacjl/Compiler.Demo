using System;
using System.Buffers.Binary;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_DOS_HEADER — DOS 头部（64 字节），字段名保留 winnt.h 原名。</summary>
    public readonly record struct ImageDosHeader(
        ushort EMagic,
        ushort ECblp,
        ushort ECp,
        ushort ECrlc,
        ushort ECparhdr,
        ushort EMinalloc,
        ushort EMaxalloc,
        ushort ESs,
        ushort ESp,
        ushort ECsum,
        ushort EIp,
        ushort ECs,
        ushort ELfarlc,
        ushort EOvno,
        byte[] ERes,
        ushort EOemid,
        ushort EOeminfo,
        byte[] ERes2,
        int ELfanew)
    {
        public static int Size => PeConstants.DosHeaderSize;

        public static ImageDosHeader Read(ReadOnlySpan<byte> s)
        {
            return new ImageDosHeader(
                BinaryPrimitives.ReadUInt16LittleEndian(s),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(2)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(6)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(8)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(10)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(12)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(14)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(16)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(18)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(20)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(22)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(24)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(26)),
                s.Slice(28, 8).ToArray(),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(36)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(38)),
                s.Slice(40, 20).ToArray(),
                BinaryPrimitives.ReadInt32LittleEndian(s.Slice(60)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d, EMagic);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(2), ECblp);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(4), ECp);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(6), ECrlc);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(8), ECparhdr);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(10), EMinalloc);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(12), EMaxalloc);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(14), ESs);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(16), ESp);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(18), ECsum);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(20), EIp);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(22), ECs);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(24), ELfarlc);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(26), EOvno);
            ERes.CopyTo(d.Slice(28, 8));
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(36), EOemid);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(38), EOeminfo);
            ERes2.CopyTo(d.Slice(40, 20));
            BinaryPrimitives.WriteInt32LittleEndian(d.Slice(60), ELfanew);
        }
    }

    /// <summary>IMAGE_FILE_HEADER — COFF 文件头（20 字节）。</summary>
    public readonly record struct ImageFileHeader(
        ushort Machine,
        ushort NumberOfSections,
        uint TimeDateStamp,
        uint PointerToSymbolTable,
        uint NumberOfSymbols,
        ushort SizeOfOptionalHeader,
        ushort Characteristics)
    {
        public static int Size => 20;

        public static ImageFileHeader Read(ReadOnlySpan<byte> s)
        {
            return new ImageFileHeader(
                BinaryPrimitives.ReadUInt16LittleEndian(s),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(2)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(12)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(16)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(18)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d, Machine);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(2), NumberOfSections);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), TimeDateStamp);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(8), PointerToSymbolTable);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(12), NumberOfSymbols);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(16), SizeOfOptionalHeader);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(18), Characteristics);
        }
    }

    /// <summary>IMAGE_DATA_DIRECTORY — 数据目录项（8 字节）。</summary>
    public readonly record struct ImageDataDirectory(uint VirtualAddress, uint Size)
    {
        public static int SizeOfEntry => 8;

        public static ImageDataDirectory Read(ReadOnlySpan<byte> s)
        {
            return new ImageDataDirectory(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, VirtualAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), Size);
        }
    }

    /// <summary>IMAGE_OPTIONAL_HEADER64 — PE32+ 可选头（240 字节），无 BaseOfData。</summary>
    public readonly record struct ImageOptionalHeader64(
        PeOptionalMagic Magic,
        byte MajorLinkerVersion,
        byte MinorLinkerVersion,
        uint SizeOfCode,
        uint SizeOfInitializedData,
        uint SizeOfUninitializedData,
        uint AddressOfEntryPoint,
        uint BaseOfCode,
        ulong ImageBase,
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
        ulong SizeOfStackReserve,
        ulong SizeOfStackCommit,
        ulong SizeOfHeapReserve,
        ulong SizeOfHeapCommit,
        uint LoaderFlags,
        uint NumberOfRvaAndSizes,
        ImageDataDirectory[] DataDirectories)
    {
        public static int Size => 112 + 16 * ImageDataDirectory.SizeOfEntry;

        public static ImageOptionalHeader64 Read(ReadOnlySpan<byte> s)
        {
            var directories = new ImageDataDirectory[16];
            for (var i = 0; i < directories.Length; i++)
            {
                directories[i] = ImageDataDirectory.Read(s.Slice(112 + i * ImageDataDirectory.SizeOfEntry));
            }

            return new ImageOptionalHeader64(
                (PeOptionalMagic)BinaryPrimitives.ReadUInt16LittleEndian(s),
                s[2],
                s[3],
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(12)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(16)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(20)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(24)),
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
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(72)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(80)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(88)),
                BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(96)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(104)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(108)),
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
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(24), ImageBase);
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
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(72), SizeOfStackReserve);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(80), SizeOfStackCommit);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(88), SizeOfHeapReserve);
            BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(96), SizeOfHeapCommit);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(104), LoaderFlags);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(108), NumberOfRvaAndSizes);

            for (var i = 0; i < 16; i++)
            {
                DataDirectories[i].Write(d.Slice(112 + i * ImageDataDirectory.SizeOfEntry));
            }
        }
    }

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

    /// <summary>IMAGE_NT_HEADERS64 — "PE\0\0" 签名 + COFF 头 + PE32+ 可选头（264 字节）。</summary>
    public readonly record struct ImageNtHeaders64(uint Signature, ImageFileHeader FileHeader, ImageOptionalHeader64 OptionalHeader)
    {
        public static int Size => 4 + ImageFileHeader.Size + ImageOptionalHeader64.Size;

        public static ImageNtHeaders64 Read(ReadOnlySpan<byte> s)
        {
            return new ImageNtHeaders64(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                ImageFileHeader.Read(s.Slice(4)),
                ImageOptionalHeader64.Read(s.Slice(24)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, Signature);
            FileHeader.Write(d.Slice(4));
            OptionalHeader.Write(d.Slice(24));
        }
    }

    /// <summary>IMAGE_NT_HEADERS32 — "PE\0\0" 签名 + COFF 头 + PE32 可选头（120 字节）。</summary>
    public readonly record struct ImageNtHeaders32(uint Signature, ImageFileHeader FileHeader, ImageOptionalHeader32 OptionalHeader)
    {
        public static int Size => 4 + ImageFileHeader.Size + ImageOptionalHeader32.Size;

        public static ImageNtHeaders32 Read(ReadOnlySpan<byte> s)
        {
            return new ImageNtHeaders32(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                ImageFileHeader.Read(s.Slice(4)),
                ImageOptionalHeader32.Read(s.Slice(24)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, Signature);
            FileHeader.Write(d.Slice(4));
            OptionalHeader.Write(d.Slice(24));
        }
    }

    /// <summary>IMAGE_SECTION_HEADER — 节表项（40 字节），Misc 按 VirtualSize 语义使用。</summary>
    public readonly record struct ImageSectionHeader(
        byte[] Name,
        uint VirtualSize,
        uint VirtualAddress,
        uint SizeOfRawData,
        uint PointerToRawData,
        uint PointerToRelocations,
        uint PointerToLinenumbers,
        ushort NumberOfRelocations,
        ushort NumberOfLinenumbers,
        uint Characteristics)
    {
        public static int Size => 40;

        public string NameString
        {
            get
            {
                var end = Array.IndexOf(Name, (byte)0);
                if (end < 0)
                {
                    end = Name.Length;
                }

                return System.Text.Encoding.ASCII.GetString(Name, 0, end);
            }
        }

        public static ImageSectionHeader Read(ReadOnlySpan<byte> s)
        {
            return new ImageSectionHeader(
                s.Slice(0, 8).ToArray(),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(12)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(16)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(20)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(24)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(28)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(32)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(34)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(36)));
        }

        public void Write(Span<byte> d)
        {
            Name.CopyTo(d.Slice(0, 8));
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(8), VirtualSize);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(12), VirtualAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(16), SizeOfRawData);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(20), PointerToRawData);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(24), PointerToRelocations);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(28), PointerToLinenumbers);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(32), NumberOfRelocations);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(34), NumberOfLinenumbers);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(36), Characteristics);
        }
    }
}