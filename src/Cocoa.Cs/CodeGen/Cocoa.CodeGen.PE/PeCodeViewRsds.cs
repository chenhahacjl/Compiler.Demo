using System;
using System.Buffers.Binary;

namespace Cocoa.CodeGen.PE
{
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
