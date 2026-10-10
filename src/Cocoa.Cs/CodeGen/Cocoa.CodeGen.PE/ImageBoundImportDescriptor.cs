using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_BOUND_IMPORT_DESCRIPTOR — 绑定导入描述符（16 字节）。</summary>
    public readonly record struct ImageBoundImportDescriptor(
        uint TimeDateStamp,
        ushort OffsetModuleName,
        ushort NumberOfModuleForwarderRefs)
    {
        public static int SizeOfEntry => 16;

        public static ImageBoundImportDescriptor Read(ReadOnlySpan<byte> s)
        {
            return new ImageBoundImportDescriptor(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(6)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, TimeDateStamp);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(4), OffsetModuleName);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(6), NumberOfModuleForwarderRefs);
        }
    }
}
