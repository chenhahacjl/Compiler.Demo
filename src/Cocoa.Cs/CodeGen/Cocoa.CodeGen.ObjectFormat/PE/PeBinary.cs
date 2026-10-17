namespace Cocoa.CodeGen.ObjectFormat.PE
{
    /// <summary>
    /// PE 二进制写辅助（收敛 PeFileWriter / PeImage / ManagedPEWriter 三处复制粘贴的 Align 与 WriteUInt*）。
    /// 统一低端序小写；三处实现此前逐字相同，消除漂移风险。
    /// </summary>
    public static class PeBinary
    {
        public static int Align(int value, int alignment)
        {
            var remainder = value % alignment;
            return remainder == 0 ? value : value + alignment - remainder;
        }

        public static uint Align(uint value, uint alignment)
        {
            var remainder = value % alignment;
            return remainder == 0 ? value : value + alignment - remainder;
        }

        public static void WriteUInt16(byte[] bytes, int offset, ushort value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
        }

        public static void WriteUInt32(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        public static uint ReadUInt32(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset]
                | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16)
                | (bytes[offset + 3] << 24));
        }

        public static void WriteUInt64(byte[] bytes, int offset, long value)
        {
            for (var i = 0; i < 8; i++)
            {
                bytes[offset + i] = (byte)(value >> (i * 8));
            }
        }

        public static int Align4(int value) => (value + 3) & ~3;

        public static void WriteUInt16(Stream stream, ushort value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
        }

        public static void WriteUInt32(Stream stream, int value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 24));
        }

        public static void WriteInt32(Stream stream, int value) => WriteUInt32(stream, value);

        public static void WriteUInt16(List<byte> bytes, ushort value)
        {
            bytes.Add((byte)value);
            bytes.Add((byte)(value >> 8));
        }

        public static void WriteUInt32(List<byte> bytes, uint value)
        {
            bytes.Add((byte)value);
            bytes.Add((byte)(value >> 8));
            bytes.Add((byte)(value >> 16));
            bytes.Add((byte)(value >> 24));
        }

        public static void WriteInt32(List<byte> bytes, int value) => WriteUInt32(bytes, (uint)value);
    }
}