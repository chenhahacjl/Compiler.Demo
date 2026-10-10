using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_DELAYLOAD_DESCRIPTOR — 延迟加载描述符（32 字节）。</summary>
    public readonly record struct ImageDelayLoadDescriptor(
        uint Attributes,
        uint DllNameRva,
        uint ModuleHandleRva,
        uint ImportAddressTableRva,
        uint ImportNameTableRva,
        uint BoundImportAddressTableRva,
        uint UnloadInformationTableRva,
        uint TimeDateStamp)
    {
        public static int SizeOfEntry => 32;

        public static ImageDelayLoadDescriptor Read(ReadOnlySpan<byte> s)
        {
            return new ImageDelayLoadDescriptor(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(12)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(16)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(20)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(24)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(28)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, Attributes);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), DllNameRva);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(8), ModuleHandleRva);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(12), ImportAddressTableRva);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(16), ImportNameTableRva);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(20), BoundImportAddressTableRva);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(24), UnloadInformationTableRva);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(28), TimeDateStamp);
        }
    }
}
