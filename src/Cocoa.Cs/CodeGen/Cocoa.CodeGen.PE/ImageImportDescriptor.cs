using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    public readonly record struct ImageImportDescriptor(
        uint OriginalFirstThunk,
        uint TimeDateStamp,
        uint ForwarderChain,
        uint Name,
        uint FirstThunk)
    {
        public static int SizeOfEntry => 20;

        public bool IsEndOfArray => OriginalFirstThunk == 0 && TimeDateStamp == 0 && ForwarderChain == 0 && Name == 0 && FirstThunk == 0;

        public static ImageImportDescriptor Read(ReadOnlySpan<byte> s)
        {
            return new ImageImportDescriptor(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(12)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(16)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, OriginalFirstThunk);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), TimeDateStamp);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(8), ForwarderChain);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(12), Name);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(16), FirstThunk);
        }
    }
}
