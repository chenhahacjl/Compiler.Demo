using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    /// <summary>IMAGE_THUNK_DATA32 — 导入项（4 字节），AddressOfData 四语义由辅助属性区分。</summary>
    public readonly record struct ImageThunkData32(uint AddressOfData)
    {
        public static int SizeOfEntry => 4;

        public bool IsNull => AddressOfData == 0;

        public bool IsOrdinal => (AddressOfData & PeConstants.OrdinalFlag32) != 0;

        public ushort OrdinalNumber => unchecked((ushort)(AddressOfData & 0xFFFF));

        public uint AddressOfDataRva => unchecked((uint)(AddressOfData & 0x7FFFFFFF));

        public static ImageThunkData32 Read(ReadOnlySpan<byte> s)
        {
            return new ImageThunkData32(BinaryPrimitives.ReadUInt32LittleEndian(s));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, AddressOfData);
        }
    }
}
