using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_RESOURCE_DIRECTORY_STRING — 资源目录名字符串（Length + UTF-16LE）。</summary>
    public readonly record struct ImageResourceDirectoryString(ushort Length, byte[] Value)
    {
        public string ValueString => Encoding.Unicode.GetString(Value);

        public static ImageResourceDirectoryString Read(ReadOnlySpan<byte> s)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(s);
            return new ImageResourceDirectoryString(length, s.Slice(2, length * 2).ToArray());
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d, Length);
            Value.CopyTo(d.Slice(2));
        }
    }
}
