using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_THUNK_DATA64 — 导入项（8 字节），AddressOfData 四语义由辅助属性区分。</summary>
    public readonly record struct ImageThunkData64(ulong AddressOfData)
    {
        public static int SizeOfEntry => 8;

        public bool IsNull => AddressOfData == 0;

        public bool IsOrdinal => (AddressOfData & PeConstants.OrdinalFlag64) != 0;

        public ushort OrdinalNumber => unchecked((ushort)(AddressOfData & 0xFFFF));

        public uint AddressOfDataRva => unchecked((uint)(AddressOfData & 0x7FFFFFFF));

        public static ImageThunkData64 Read(ReadOnlySpan<byte> s)
        {
            return new ImageThunkData64(BinaryPrimitives.ReadUInt64LittleEndian(s));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(d, AddressOfData);
        }
    }
}
