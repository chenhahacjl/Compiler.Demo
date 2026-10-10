using System;
using System.Buffers.Binary;

namespace Cocoa.CodeGen.PE
{
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
}
