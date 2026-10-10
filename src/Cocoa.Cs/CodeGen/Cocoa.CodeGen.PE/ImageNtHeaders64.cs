using System;
using System.Buffers.Binary;

namespace Cocoa.CodeGen.PE
{
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
}
