namespace Cocoa.CodeGen.PE
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

        public static void WriteUInt64(byte[] bytes, int offset, long value)
        {
            for (var i = 0; i < 8; i++)
            {
                bytes[offset + i] = (byte)(value >> (i * 8));
            }
        }
    }
}