using Cocoa.CodeGen.ObjectFormat.PE;
using Cocoa.Targeting;
using System;
using System.Linq;
using Xunit;

namespace Cocoa.Tests.CodeGen.PE
{
    public class PeObjectWriterTests
    {
        private static ObjectWriteOptions BasicOptions(Architecture arch)
        {
            var options = new ObjectWriteOptions(
                TargetOS.Windows, arch,
                arch == Architecture.X64 ? 0x140000000UL : 0x400000UL,
                3 /* WindowsCui */,
                0x1000, 0x1000, 0x200)
            {
                AdditionalFileCharacteristics = (ushort)(PeFileCharacteristics.ExecutableImage | (arch == Architecture.X64 ? 0 : PeFileCharacteristics.Machine32Bit)),
            };
            options.Sections.Add(new SectionSpec(".text", new byte[0x200], 0x1000, 0x60000020));
            return options;
        }

        [Fact]
        public void Writes_Pe32Plus_With_CheckSum_And_TimeStamp()
        {
            var image = PeObjectWriter.Instance.Write(BasicOptions(Architecture.X64));

            // PE 头
            var eLfanew = BitConverter.ToInt32(image, 0x3C);
            Assert.Equal("MZ", System.Text.Encoding.ASCII.GetString(image, 0, 2));
            Assert.Equal(0x20B, BitConverter.ToUInt16(image, eLfanew + 24)); // PE32+ magic

            // TimeDateStamp 已填（非零）
            var timestamp = BitConverter.ToUInt32(image, eLfanew + 8);
            Assert.True(timestamp > 0, "TimeDateStamp should be filled");

            // CheckSum 非零
            var checkSumOffset = eLfanew + 4 + 20 + 64;
            var checksum = BitConverter.ToUInt32(image, checkSumOffset);
            Assert.True(checksum != 0, "CheckSum should be computed");

            // 不动点：重算（跳过该字段）应等于已写值
            var recomputed = PeChecksum.Compute(image, checkSumOffset);
            Assert.Equal(checksum, recomputed);
        }

        [Fact]
        public void Writes_Pe32_For_AnyCpuX86()
        {
            foreach (var arch in new[] { Architecture.AnyCPU, Architecture.X86 })
            {
                var image = PeObjectWriter.Instance.Write(BasicOptions(arch));
                var eLfanew = BitConverter.ToInt32(image, 0x3C);
                Assert.Equal(0x10B, BitConverter.ToUInt16(image, eLfanew + 24)); // PE32 magic
                var checksum = BitConverter.ToUInt32(image, eLfanew + 4 + 20 + 64);
                Assert.True(checksum != 0);
            }
        }

        [Fact]
        public void Image_Is_Readable_By_PeImageReader()
        {
            var image = PeObjectWriter.Instance.Write(BasicOptions(Architecture.X64));
            var reader = PeImageReader.TryOpen(image);
            Assert.NotNull(reader);
            Assert.True(reader!.IsPe32Plus);
            Assert.Equal(0x140000000UL, reader.ImageBase);
        }

        [Fact]
        public void CheckSum_Matches_Reference_Algorithm()
        {
            // 确定性验证：构造已知小镜像（含 ASCII 数据），与独立实现对照。
            var image = new byte[0x400];
            for (var i = 0; i < 0x400; i++) image[i] = (byte)(i * 7 % 251);
            BitConverter.GetBytes((int)0x100).CopyTo(image, 0x3C); // e_lfanew
            BitConverter.GetBytes((int)0x18).CopyTo(image, 0x3C + 4 + 20 + 64); // 该字段本身参与和（读置 0）

            var checksum = PeChecksum.Compute(image, 0x3C + 4 + 20 + 64);

            // 独立慢速实现（一补和）
            var expected = 0L;
            for (var i = 0; i < image.Length / 4; i++)
            {
                var off = i * 4;
                var value = BitConverter.ToUInt32(image, off);
                if (off == 0x3C + 4 + 20 + 64) value = 0;
                expected += (value & 0xFFFF) + (value >> 16);
                expected = (expected & 0xFFFF) + (expected >> 16);
            }
            if (image.Length % 4 != 0)
            {
                uint last = 0;
                for (var i = 0; i < image.Length % 4; i++) last |= (uint)image[image.Length / 4 * 4 + i] << (i * 8);
                expected += (last & 0xFFFF) + (last >> 16);
                expected = (expected & 0xFFFF) + (expected >> 16);
            }
            expected = (expected & 0xFFFF) + (expected >> 16);
            expected += (uint)image.Length;
            expected = (expected & 0xFFFF) + (expected >> 16);

            Assert.Equal((uint)expected, checksum);
        }
    }
}
