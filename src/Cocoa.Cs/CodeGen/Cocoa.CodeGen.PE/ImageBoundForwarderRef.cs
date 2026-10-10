using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_BOUND_FORWARDER_REF — 绑定转发表项（8 字节）。</summary>
    public readonly record struct ImageBoundForwarderRef(uint TimeDateStamp, ushort OffsetModuleName)
    {
        public static int SizeOfEntry => 8;

        public static ImageBoundForwarderRef Read(ReadOnlySpan<byte> s)
        {
            return new ImageBoundForwarderRef(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(4)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, TimeDateStamp);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(4), OffsetModuleName);
        }
    }
}
