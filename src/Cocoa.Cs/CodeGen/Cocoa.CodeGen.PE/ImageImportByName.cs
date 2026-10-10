using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_IMPORT_BY_NAME — Hint + 以 NUL 结尾的函数名。</summary>
    public readonly record struct ImageImportByName(ushort Hint, byte[] Name)
    {
        public int Size => 2 + Name.Length + 1;

        public string NameString => Encoding.ASCII.GetString(Name);

        public static ImageImportByName Read(ReadOnlySpan<byte> s)
        {
            var hint = BinaryPrimitives.ReadUInt16LittleEndian(s);
            var name = s.Slice(2);
            var end = name.IndexOf((byte)0);
            if (end < 0)
            {
                end = name.Length;
            }

            return new ImageImportByName(hint, name.Slice(0, end).ToArray());
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d, Hint);
            Name.CopyTo(d.Slice(2));
            d[2 + Name.Length] = 0;
        }
    }
}
