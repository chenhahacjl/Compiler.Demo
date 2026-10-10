using System;
using System.Buffers.Binary;

namespace Cocoa.CodeGen.PE
{
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
}
