using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Cocoa.CodeGen.ObjectFormat.PE
{
    /// <summary>
    /// 最小 Windows 原生 PDB 7.0 写入器（MSF 容器 + DS/TPI/DBI/IPI + CodeView 符号流）。
    /// 交付：函数符号（名 → RVA/段 + 长度），供 IDA/WinDbg 加载；行号仍在 exe 内嵌 COFF 符号表承载。
    /// 布局对齐 llvm (DbiStreamBuilder/TPIStreamBuilder) 与 microsoft-pdb 结构。
    /// </summary>
    public static class PdbWriter
    {
        private const int BlockSize = 0x1000;
        private const uint DbiVersion = 19990903;
        private const uint TpiVersion = 19990903;
        private const uint PdbImplVersion = 20000404;

        private const ushort SymObjectName = 0x1101;
        private const ushort SymEnd = 0x0006;
        private const ushort SymGlobalProc32 = 0x1110;
        private const ushort SymLines = 0x1105;

        /// <summary>函数符号输入（与 COFF 表同源）：名 / RVA / 代码内偏移 / 长度 / 源行号。</summary>
        public readonly record struct PdbFunctionSymbol(string Name, uint Rva, int Length, int Line = 0);

        /// <summary>确定性 GUID（与 RSDS 一致）：MD5(moduleName) 前 16 字节。</summary>
        public static Guid MakeGuid(string moduleName)
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(moduleName));
            return new Guid(hash.AsSpan(0, 16));
        }

        /// <summary>构建 .pdb 字节。functions 为空返回 null（不生成）。</summary>
        public static byte[]? Build(string moduleName, IReadOnlyList<PdbFunctionSymbol> functions, int codeLength)
        {
            if (functions.Count == 0)
            {
                return null;
            }

            var guid = MakeGuid(moduleName);
            var ds = BuildDsStream(guid);
            var tpi = BuildTpiStream();
            var dbi = BuildDbiStream(moduleName);
            var symbols = BuildSymbolStream(moduleName, functions, codeLength);

            // MSF 容器：stream 0=DS 1=TPI 2=DBI 3=IPI(空) 4=Symbols
            var streams = new[] { ds, tpi, dbi, Array.Empty<byte>(), symbols };
            return PackMsf(streams);
        }

        // ------------------------------------------------------------------
        // 各流
        // ------------------------------------------------------------------

        private static byte[] BuildDsStream(Guid guid)
        {
            var b = new MemoryStream();
            WriteU32(b, PdbImplVersion);
            WriteU32(b, 0);                       // Signature（时间戳，可 0）
            WriteU32(b, 1);                       // Age
            b.Write(guid.ToByteArray(), 0, 16);
            WriteU32(b, 0);                       // 无命名流
            return b.ToArray();
        }

        private static byte[] BuildTpiStream()
        {
            var b = new MemoryStream();
            WriteU32(b, TpiVersion);
            WriteU32(b, 48);                      // HeaderSize
            WriteU32(b, 0x1000);                  // TypeIndexBegin
            WriteU32(b, 0x1000);                  // TypeIndexEnd（无类型记录）
            WriteU32(b, 0);                       // TypeRecordBytes
            WriteU16(b, 0xFFFF);                  // HashStreamIndex
            WriteU16(b, 0xFFFF);                  // HashAuxStreamIndex
            WriteU32(b, 2);                       // HashKeySize
            WriteU32(b, 0);                       // NumHashBuckets
            WriteU32(b, 0);                       // HashValueBufferOffset
            WriteU32(b, 0);                       // HashValueBufferLength
            WriteU32(b, 0);                       // IndexOffsetBufferOffset
            WriteU32(b, 0);                       // IndexOffsetBufferLength
            return b.ToArray();
        }

        private static byte[] BuildDbiStream(string moduleName)
        {
            var b = new MemoryStream();
            // DBI 头（60 字节，版本 19990903）
            WriteU32(b, 0xFFFFFFFF);              // Magic
            WriteU32(b, DbiVersion);
            WriteU32(b, 0xFFFFFFFF);              // GlobalStreamIndex
            WriteU32(b, 4);                       // SymRecordStreamIndex（模块符号流 = 4）
            WriteU32(b, 0);                       // PdbDllVersion
            WriteU32(b, 0xFFFFFFFF);              // ModiStreamIndex
            WriteU32(b, 0xFFFFFFFF);              // OptionalDbgHeaderStreamIndex
            WriteU32(b, 0);                       // Flags
            WriteU16(b, 0x8664);                  // Machine = AMD64
            WriteU16(b, 0);                       // Reserved
            WriteU32(b, 60);                      // Headersize
            WriteU32(b, 0xFFFFFFFF);              // SubSectionStreamIndex
            WriteU32(b, 0);                       // OffsetToPublicSymsTable
            WriteU32(b, 0xFFFFFFFF);              // PublicSymbolStreamIndex
            WriteU32(b, 0);                       // OffsetToSymRecords
            WriteU32(b, 0xFFFFFFFF);              // SectionContributionsStreamIndex

            // 模块信息（DbiModuleDescriptor 20 字节）+ 模块名
            WriteU32(b, 0);                       // Unused1
            WriteU32(b, 4);                       // ModiStreamIndex（本模块符号流）
            WriteU32(b, 0);                       // OldStreamIndex
            WriteU32(b, 0xFFFFFFFF);              // GlobalStreamIndex
            WriteU32(b, 0);                       // Unused2
            WriteU16(b, 0);                       // StreamNameOffset
            WriteU16(b, 0);                       // Unused3
            foreach (var c in moduleName)
            {
                b.WriteByte((byte)c);
            }

            b.WriteByte(0);
            while (b.Position % 4 != 0)
            {
                b.WriteByte(0);
            }

            // DBI 字符串表（空）
            WriteU32(b, 0);
            return b.ToArray();
        }

        private static byte[] BuildSymbolStream(string moduleName, IReadOnlyList<PdbFunctionSymbol> functions, int codeLength)
        {
            var b = new MemoryStream();
            var pEndFieldOffsets = new List<int>();
            var endRecordStarts = new List<int>();

            // S_OBJNAME：模块名
            WriteCvHeader(b, SymObjectName, 4 + moduleName.Length + 1);
            WriteU32(b, 0);                       // signature
            WriteAscii(b, moduleName);
            Align4(b);

            // S_GPROC32 × 函数 + S_END
            foreach (var fn in functions)
            {
                var recordStart = (int)b.Position;
                var payload = 28 + 4 + 2 + 2 + fn.Name.Length + 1; // pParent..rett + name
                WriteCvHeader(b, SymGlobalProc32, payload);
                WriteU32(b, 0);                   // pParent
                pEndFieldOffsets.Add(recordStart + 4 + 4); // pEnd 字段（rectyp 后偏移 4）
                WriteU32(b, 0);                   // pEnd 占位
                WriteU32(b, 0);                   // pNext
                WriteU32(b, (uint)Math.Max(0, fn.Length)); // proc 长度
                WriteU32(b, 0);                   // DbgStart
                WriteU32(b, (uint)Math.Max(0, fn.Length)); // DbgEnd
                WriteU32(b, 0);                   // typind
                WriteU32(b, fn.Rva);              // off（RVA）
                WriteU16(b, 1);                   // seg = .text
                b.WriteByte(0);                   // flags
                b.WriteByte(0);                   // rett
                WriteAscii(b, fn.Name);
                Align4(b);

                // S_END
                endRecordStarts.Add((int)b.Position);
                WriteU16(b, 2);                   // reclen（不含本字段 = rectyp 2 字节）
                WriteU16(b, SymEnd);
            }

            // S_LINES：行号表（file id=0 单文件；段内偏移 + 语句行）。供 WinDbg/IDA 源码级行号。
            {
                var ordered = functions.OrderBy(f => f.Rva).Where(f => f.Line > 0).ToList();
                if (ordered.Count > 0)
                {
                    var payload = 4 + 2 + 2 + 8 * ordered.Count;
                    WriteCvHeader(b, SymLines, payload);
                    WriteU32(b, 0);                       // file id（模块文件表索引；最小实现单文件 0）
                    WriteU16(b, (ushort)ordered.Count);   // numLines
                    WriteU16(b, 0);                       // fileSize
                    foreach (var fn in ordered)
                    {
                        WriteU32(b, (uint)(fn.Rva - PeFileWriter.TextRva)); // 段内偏移（.text 段内）
                        WriteU32(b, (uint)Math.Max(1, fn.Line) | 0x80000000u); // 语句行（bit31 标记）
                    }

                    Align4(b);
                }
            }

            // 回填每个 S_GPROC32 的 pEnd = 对应 S_END 记录起点偏移。
            var bytes = b.ToArray();
            for (var i = 0; i < pEndFieldOffsets.Count; i++)
            {
                WriteU32At(bytes, pEndFieldOffsets[i], (uint)endRecordStarts[i]);
            }

            return bytes;
        }

        // ------------------------------------------------------------------
        // MSF 容器
        // ------------------------------------------------------------------

        private static byte[] PackMsf(IReadOnlyList<byte[]> streams)
        {
            var streamSizes = streams.Select(s => s.Length).ToArray();
            var blocksPerStream = streams.Select(s => (s.Length + BlockSize - 1) / BlockSize).ToArray();
            var totalStreamBlocks = blocksPerStream.Sum();

            // 流目录 = [count][sizes...][块号...]
            var dirStreamBytes = 4 + 4 * streams.Count + 4 * totalStreamBlocks;
            var dirBlocks = (dirStreamBytes + BlockSize - 1) / BlockSize;

            // 块分配：0=头 1=块映射 2..(2+dirBlocks-1)=目录 之后=流数据
            var nextBlock = 2 + dirBlocks;
            var streamBlockStarts = new List<int>(streams.Count);
            var allStreamBlocks = new List<int>();
            for (var i = 0; i < streams.Count; i++)
            {
                streamBlockStarts.Add(nextBlock);
                for (var k = 0; k < blocksPerStream[i]; k++)
                {
                    allStreamBlocks.Add(nextBlock++);
                }
            }

            var totalBlocks = nextBlock;
            var image = new byte[totalBlocks * BlockSize];

            // 头：32 字节魔数 "Microsoft C/C++ MSF 7.00\r\n\x1aDS\0\0"（\x1a 用字节字面量，ASCII 编码会丢控制符）
            var magic = new byte[]
            {
                (byte)'M', (byte)'i', (byte)'c', (byte)'r', (byte)'o', (byte)'s', (byte)'o', (byte)'f', (byte)'t',
                (byte)' ', (byte)'C', (byte)'/', (byte)'C', (byte)'+', (byte)'+', (byte)' ', (byte)'M', (byte)'S',
                (byte)'F', (byte)' ', (byte)'7', (byte)'.', (byte)'0', (byte)'0',
                0x0D, 0x0A, 0x1A, (byte)'D', (byte)'S', 0x00, 0x00, 0x00,
            };
            Array.Copy(magic, image, magic.Length);

            // 目录内容
            var dir = new MemoryStream();
            WriteU32(dir, (uint)streams.Count);
            foreach (var size in streamSizes)
            {
                WriteU32(dir, (uint)size);
            }

            foreach (var block in allStreamBlocks)
            {
                WriteU32(dir, (uint)block);
            }

            var dirBytes = dir.ToArray();

            // 块映射（块 1）：目录所在块号
            var blockMap = new byte[BlockSize];
            for (var i = 0; i < dirBlocks; i++)
            {
                WriteU32At(blockMap, i * 4, (uint)(2 + i));
            }

            Array.Copy(blockMap, 0, image, 1 * BlockSize, BlockSize);

            // 写目录块
            for (var i = 0; i < dirBlocks; i++)
            {
                var offset = i * BlockSize;
                var count = Math.Min(BlockSize, dirBytes.Length - offset);
                Array.Copy(dirBytes, offset, image, (2 + i) * BlockSize, count);
            }

            // 写流数据
            for (var i = 0; i < streams.Count; i++)
            {
                var start = streamBlockStarts[i] * BlockSize;
                var data = streams[i];
                for (var k = 0; k < data.Length; k++)
                {
                    image[start + k] = data[k];
                }
            }

            return image;
        }

        // ------------------------------------------------------------------
        // CV 记录辅助
        // ------------------------------------------------------------------

        private static void WriteCvHeader(MemoryStream b, ushort type, int payloadBytes)
        {
            // reclen 不含 reclen 字段本身（含 rectyp + payload），再补 4 对齐
            var len = 2 + payloadBytes;
            WriteU16(b, (ushort)Align4Int(len));
            WriteU16(b, type);
        }

        private static void Align4(MemoryStream b)
        {
            while (b.Position % 4 != 0)
            {
                b.WriteByte(0);
            }
        }

        private static int Align4Int(int v) => (v + 3) & ~3;

        private static void WriteAscii(MemoryStream b, string s)
        {
            foreach (var c in s)
            {
                b.WriteByte((byte)c);
            }

            b.WriteByte(0);
        }

        private static void WriteU32(MemoryStream b, uint v)
        {
            b.WriteByte((byte)v);
            b.WriteByte((byte)(v >> 8));
            b.WriteByte((byte)(v >> 16));
            b.WriteByte((byte)(v >> 24));
        }

        private static void WriteU16(MemoryStream b, ushort v)
        {
            b.WriteByte((byte)v);
            b.WriteByte((byte)(v >> 8));
        }

        private static void WriteU32At(byte[] b, int offset, uint v)
        {
            b[offset] = (byte)v;
            b[offset + 1] = (byte)(v >> 8);
            b[offset + 2] = (byte)(v >> 16);
            b[offset + 3] = (byte)(v >> 24);
        }
    }
}
