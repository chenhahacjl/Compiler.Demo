using System;

namespace Cocoa.CodeGen.ObjectFormat.PE
{
    /// <summary>
    /// Rich 头生成器：在 DOS stub 之后、PE 签名之前插入标准 Rich 头（DanS + checksum + XOR 条目 + Rich + key），
    /// 并把 e_lfanew 固定到 <see cref="ELfanew"/>（MSVC 常见值 0x100），使产物呈现"已知编译器产出"特征。
    /// 产品/构建 ID 为自定义值（不冒充 MSVC），key 为固定常量——输出确定性、可复现。
    /// 布局：
    ///   [0x40 .. 0x7D]  DOS stub message
    ///   [0x7E ..]       DanS + checksum + N×条目（均 XOR key）→ 连续
    ///   [.. 0xF7]       0 填充
    ///   [0xF8 .. 0xFF]  "Rich"(4B) + key(4B)，紧贴 e_lfanew
    /// </summary>
    public static class RichHeaderBuilder
    {
        /// <summary>固定 e_lfanew（PE 签名位置）。</summary>
        public const int ELfanew = 0x100;

        private const int DosHeaderSize = 64;
        private const int StubMessageLength = 62; // WriteDosStub 写出的消息字节数（0x40..0x7D）
        private const int StubEnd = DosHeaderSize + StubMessageLength; // 0x7E

        private const int DanSSignature = unchecked((int)0x536E6144); // "DanS"
        private const int RichSignature = unchecked((int)0x68636952); // "Rich"

        /// <summary>自定义编译工具链标识（ProductID / BuildID / 条目重复次数 / XOR key）。</summary>
        private const int ProductId = 0xC0CA;
        private const int BuildId = 0x0001;
        private const int EntryCount = 3;
        private const int XorKey = 0x0C0C0C0C;

        /// <summary>
        /// 把 Rich 头插入已生成的镜像：把 [旧 e_lfanew .. headerEnd) 整块右移到新位置，
        /// 在空出的区域写 Rich 数据并回写 e_lfanew。返回新的 e_lfanew。
        /// </summary>
        /// <param name="headerEnd">节表末端在头区内的偏移（含全部节表项），保证右移后的头区不越出 SizeOfHeaders。</param>
        /// <param name="headersSize">SizeOfHeaders（头区预算），右移后的节表末端必须落在此内。</param>
        /// <exception cref="InvalidOperationException">镜像尺寸不足以容纳右移后的头区。</exception>
        public static int InsertInto(byte[] image, int headerEnd, int headersSize)
        {
            var current = (int)PeBinary.ReadUInt32(image, 0x3C);
            if (current >= ELfanew)
            {
                return current;
            }

            var delta = ELfanew - current;
            if (headerEnd + delta > headersSize || headersSize > image.Length)
            {
                throw new InvalidOperationException(
                    $"Rich header insertion needs header region up to {headerEnd + delta}, SizeOfHeaders is {headersSize}.");
            }

            // 重叠右移（Array.Copy 语义 = memmove）——只搬头区 [current .. headerEnd)，绝不过界到节原始数据。
            Array.Copy(image, current, image, ELfanew, headerEnd - current);
            WriteRichData(image);
            PeBinary.WriteUInt32(image, 0x3C, ELfanew);
            return ELfanew;
        }

        /// <summary>写 Rich 数据到 [StubEnd .. ELfanew)。区域其余字节保持 0。</summary>
        private static void WriteRichData(byte[] image)
        {
            var value = (BuildId << 16) | ProductId;
            var checksum = value * EntryCount;

            var offset = StubEnd;
            PeBinary.WriteUInt32(image, offset, DanSSignature);
            offset += 4;
            PeBinary.WriteUInt32(image, offset, checksum ^ XorKey);
            offset += 4;
            for (var i = 0; i < EntryCount; i++)
            {
                PeBinary.WriteUInt32(image, offset, value ^ XorKey);
                offset += 4;
            }

            // 结尾固定 "Rich" + key（紧贴 e_lfanew）
            PeBinary.WriteUInt32(image, ELfanew - 8, RichSignature);
            PeBinary.WriteUInt32(image, ELfanew - 4, XorKey);
        }
    }
}
