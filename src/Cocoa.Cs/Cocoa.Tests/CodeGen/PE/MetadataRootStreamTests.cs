using Cocoa.CodeGen.PE;
using System;
using System.Collections.Generic;
using System.Text;
using Xunit;

namespace Cocoa.Tests.CodeGen.PE
{
    /// <summary>
    /// M1：MetadataRootStream 裸根 round-trip——任意流集写→读逐字节一致、偏移正确、同名去重。
    /// 是 IL 元数据根（BSJB）与 .cocoa 根（"COCOA"）共用的流集合基础设施。
    /// </summary>
    public class MetadataRootStreamTests
    {
        private static byte[] BuildRoot(IReadOnlyList<(string, byte[])> streams, int magicHeaderSize = 0x0A)
        {
            var headerBlockSize = magicHeaderSize;
            for (var i = 0; i < streams.Count; i++)
            {
                headerBlockSize += 8 + ((Encoding.ASCII.GetByteCount(streams[i].Item1) + 1 + 3) & ~3);
            }

            var offsets = MetadataRootStream.ComputeOffsets(headerBlockSize, streams);

            var lastDataEnd = headerBlockSize;
            for (var i = 0; i < streams.Count; i++)
            {
                var end = offsets[i] + ((streams[i].Item2.Length + 3) & ~3);
                if (end > lastDataEnd) lastDataEnd = end;
            }

            var root = new byte[lastDataEnd];
            MetadataRootStream.WriteHeaders(root, 0, magicHeaderSize, streams, offsets);
            MetadataRootStream.WriteData(root, 0, streams, offsets);
            return root;
        }

        private static byte[] StringToBytes(string s) => Encoding.UTF8.GetBytes(s);

        [Fact]
        public void RoundTrip_MultipleStreams_ByteIdentical()
        {
            var streams = new List<(string, byte[])>
            {
                ("#Strings", StringToBytes("hello\0world\0")),
                ("#Types", new byte[] { 1, 2, 3, 4 }),
                ("#US", StringToBytes("cocoa\0")),
                ("#Docs", new byte[0]), // 空流
            };

            var root = BuildRoot(streams);

            var headers = MetadataRootStream.ReadHeaders(root, streams.Count, 0x0A);
            Assert.Equal(streams.Count, headers.Count);

            for (var i = 0; i < streams.Count; i++)
            {
                Assert.Equal(streams[i].Item1, headers[i].Name);
                Assert.Equal((streams[i].Item2.Length + 3) & ~3, headers[i].Size); // size 为 4 对齐后
                var data = MetadataRootStream.ReadStream(root, headers[i]);
                // 读回数据区长度 = 对齐后 size；前 Item2.Length 字节与源一致
                Assert.Equal((streams[i].Item2.Length + 3) & ~3, data.Length);
                Assert.Equal(streams[i].Item2, data.AsSpan(0, streams[i].Item2.Length).ToArray());
            }
        }

        [Fact]
        public void RoundTrip_OffsetAlignment_FirstStreamAfterHeaders()
        {
            var streams = new List<(string, byte[])>
            {
                ("#Strings", StringToBytes("abc")),
                ("#Blob", new byte[] { 9, 9 }),
            };
            const int magicHeaderSize = 0x0A;

            // headerBlockSize = 魔数区(0x0A) + 流头表总长（每条 8 + Align4(name\0)）
            var headerBlockSize = magicHeaderSize;
            foreach (var (name, _) in streams)
            {
                headerBlockSize += 8 + ((Encoding.ASCII.GetByteCount(name) + 1 + 3) & ~3);
            }

            var offsets = MetadataRootStream.ComputeOffsets(headerBlockSize, streams);

            // 不变量：首个流紧跟流头表区并 4 字节对齐；次流紧跟首流数据对齐后。
            Assert.Equal(Align4(headerBlockSize), offsets[0]);
            Assert.Equal(Align4(headerBlockSize) + 4, offsets[1]); // "#Strings" 3B → Align4=4
        }

        private static int Align4(int value) => (value + 3) & ~3;

        [Fact]
        public void RoundTrip_DuplicateStreamName_KeepsFirst()
        {
            var streams = new List<(string, byte[])>
            {
                ("#Types", new byte[] { 1 }),
                ("#Types", new byte[] { 2 }),
            };

            var root = BuildRoot(streams);

            var headers = MetadataRootStream.ReadHeaders(root, streams.Count, 0x0A);
            Assert.Single(headers);
            Assert.Equal(4, headers[0].Size); // Align4(1)
            var data = MetadataRootStream.ReadStream(root, headers[0]);
            Assert.Equal(1, data[0]);
            Assert.Equal(new byte[] { 1 }, data.AsSpan(0, 1).ToArray());
        }

        [Fact]
        public void RoundTrip_MagicHeaderPrefix_NotOverwritten()
        {
            // 魔数头区（前 0x0A 字节）在写流头/数据后保持原样（由调用方预留）
            var streams = new List<(string, byte[])>
            {
                ("#Strings", StringToBytes("x")),
            };

            // headerBlockSize = 魔数区(0x0A) + 流头表（#Strings name 8 字节含\0 → Align4=8，+8 = 16） = 0x1A
            var headerBlockSize = 0x0A + (8 + ((8 + 3) & ~3));

            var root = new byte[0x24];
            // 预填魔数头 "COCOA" + 版本
            Encoding.ASCII.GetBytes("COCOA").CopyTo(root, 0);
            root[5] = 1;

            var offsets = MetadataRootStream.ComputeOffsets(headerBlockSize, streams);
            MetadataRootStream.WriteHeaders(root, 0, 0x0A, streams, offsets);
            MetadataRootStream.WriteData(root, 0, streams, offsets);

            Assert.Equal("COCOA", Encoding.ASCII.GetString(root, 0, 5));
            Assert.Equal(1, root[5]);
            var data = MetadataRootStream.ReadStream(root, MetadataRootStream.ReadHeaders(root, 1, 0x0A)[0]);
            Assert.Equal((int)'x', data[0]);
            Assert.Equal(Encoding.ASCII.GetBytes("x"), data.AsSpan(0, 1).ToArray());
        }
    }
}
