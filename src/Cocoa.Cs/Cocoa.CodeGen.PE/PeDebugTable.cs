using System;
using System.Buffers.Binary;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_DEBUG_DIRECTORY — 调试目录项（28 字节）。</summary>
    public readonly record struct ImageDebugDirectory(
        uint Characteristics,
        uint TimeDateStamp,
        ushort MajorVersion,
        ushort MinorVersion,
        PeDebugType Type,
        uint SizeOfData,
        uint AddressOfRawData,
        uint PointerToRawData)
    {
        public static int SizeOfEntry => 28;

        public static ImageDebugDirectory Read(ReadOnlySpan<byte> s)
        {
            return new ImageDebugDirectory(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(8)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(10)),
                (PeDebugType)BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(12)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(16)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(20)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(24)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, Characteristics);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), TimeDateStamp);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(8), MajorVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(10), MinorVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(12), (uint)Type);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(16), SizeOfData);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(20), AddressOfRawData);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(24), PointerToRawData);
        }
    }

    /// <summary>"RSDS" 调试信息（CodeView PDB 路径）。</summary>
    public readonly record struct PeCodeViewRsds(uint Signature, Guid Guid, uint Age, byte[] Path)
    {
        public const uint RsdsSignature = 0x53445352; // "RSDS"

        public int Size => 4 + 16 + 4 + Path.Length + 1;

        public string PathString => System.Text.Encoding.UTF8.GetString(Path);

        public static PeCodeViewRsds Read(ReadOnlySpan<byte> s)
        {
            var path = s.Slice(24);
            var end = path.IndexOf((byte)0);
            if (end < 0)
            {
                end = path.Length;
            }

            return new PeCodeViewRsds(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                new Guid(s.Slice(4, 16)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(20)),
                path.Slice(0, end).ToArray());
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, Signature);
            Guid.TryWriteBytes(d.Slice(4, 16));
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(20), Age);
            Path.CopyTo(d.Slice(24));
            d[24 + Path.Length] = 0;
        }
    }
}