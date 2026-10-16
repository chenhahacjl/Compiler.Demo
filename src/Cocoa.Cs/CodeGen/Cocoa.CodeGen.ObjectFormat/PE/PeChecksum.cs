using System;

namespace Cocoa.CodeGen.ObjectFormat.PE
{
    /// <summary>
    /// PE 校验和（CheckSumMappedFile 语义）：16 位一补和（进位回卷），跳过 CheckSum 字段（PE 头偏移 64），
    /// 奇数字节尾部补零，最终回卷为 16 位并加文件长度。与 Windows ImageHlp.CheckSumMappedFile 一致。
    /// </summary>
    public static class PeChecksum
    {
        /// <summary>计算镜像校验和。checkSumFieldOffset = PE 可选头 CheckSum 字段的文件偏移（PE32/PE32+ 均为 64）。</summary>
        public static uint Compute(ReadOnlySpan<byte> image, int checkSumFieldOffset)
        {
            var checksum = 0L;
            var length = image.Length;
            var fullWordCount = length / 4;
            var index = 0;

            while (index < fullWordCount)
            {
                var dword = BitConverter.ToUInt32(image.Slice(index * 4, 4));
                if (index * 4 == checkSumFieldOffset)
                {
                    // 跳过 CheckSum 字段自身（读时置 0）
                    dword = 0;
                }

                checksum += (dword & 0xFFFF) + (dword >> 16);
                // 16 位进位回卷（一补和）
                checksum = (checksum & 0xFFFF) + (checksum >> 16);
                index++;
            }

            if (index * 4 < length)
            {
                // 尾部奇数字节：补零到 4 字节
                var trailing = length - index * 4;
                uint last = 0;
                for (var i = 0; i < trailing; i++)
                {
                    last |= (uint)image[index * 4 + i] << (i * 8);
                }

                checksum += (last & 0xFFFF) + (last >> 16);
                checksum = (checksum & 0xFFFF) + (checksum >> 16);
            }

            checksum = (checksum & 0xFFFF) + (checksum >> 16);
            checksum += (uint)length;
            checksum = (checksum & 0xFFFF) + (checksum >> 16);
            return (uint)checksum;
        }
    }
}
