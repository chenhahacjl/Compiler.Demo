using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Cocoa.CodeAnalysis.Emit.Native.PEFile;

namespace Cocoa.CodeAnalysis.Emit.IL
{
    /// <summary>
    /// 托管 PE 写入器：组装 .text 段（CLR 头 + 方法体 + 元数据根 + 流）+ IMAGE_COR20_HEADER + PE 壳。
    /// 布局参考 Roslyn ManagedPEBuilder / System.Reflection.Metadata.Ecma335 的序列化顺序。
    /// </summary>
    internal static class ManagedPEWriter
    {
        private const uint CorHdrSignature = 0x424A5342; // "BSJB"
        private const uint CorHdrMajorVersion = 1;
        private const uint CorHdrMinorVersion = 1;
        private const string RuntimeVersion = "v4.0.30319";

        public const uint TextRva = 0x1000;
        private const int CorHeaderSize = 72; // IMAGE_COR20_HEADER

        /// <summary>方法体（已编码字节 + 局部变量签名 token + 最大栈）。</summary>
        internal sealed class MethodBodyBlob
        {
            public MethodBodyBlob(byte[] code, uint localVarSigToken, ushort maxStack)
            {
                Code = code;
                LocalVarSigToken = localVarSigToken;
                MaxStack = maxStack;
            }

            public byte[] Code { get; }
            public uint LocalVarSigToken { get; }
            public ushort MaxStack { get; }
        }

        /// <summary>
        /// 生成托管 exe。
        /// </summary>
        /// <param name="moduleName">模块名（不含扩展名）。</param>
        /// <param name="methods">方法定义（顺序与 <paramref name="methodBodies"/> 一致）。</param>
        /// <param name="methodBodies">每个方法的编码后方法体。</param>
        /// <param name="metadata">元数据构建器（已收集全部引用/方法）。</param>
        /// <param name="entryPointToken">入口点 MethodDef token。</param>
        /// <param name="target">目标运行时：netfx 追加 mscoree.dll!CorExeMain 导入表（Windows 直接激活 CLR），netcore 无导入。</param>
        public static byte[] Build(
            string moduleName,
            IReadOnlyList<IlMethodDef> methods,
            IReadOnlyList<MethodBodyBlob> methodBodies,
            MetadataBuilder metadata,
            uint entryPointToken,
            IlTarget target)
        {
            // 6d-4：netfx 直接运行（mscoree 导入 + I386/PE32）因 CLR 4.8 元数据兼容性问题暂不可用，
            // 当前 netfx 目标沿用 netcore 布局（AMD64 + runtimeconfig，dotnet x.exe 运行）。
            var textRva = TextRva;

            // ---- .text 布局 ----
            var section = new MemoryStream();

            // 1. CLR 头（72 字节，放在 .text 开头）
            var corHeaderOffset = (int)section.Position;
            var corHeaderRva = (uint)(textRva + corHeaderOffset);
            section.Write(new byte[CorHeaderSize]);

            // 2. 方法体区（4 字节对齐；Fat 头 12 字节 + 代码）
            while (section.Position % 4 != 0) section.WriteByte(0);
            var methodStreamOffset = (int)section.Position;
            var methodStreamRva = (uint)(textRva + methodStreamOffset);

            var bodyOffsets = new List<int>();
            var methodRvas = new Dictionary<IlMethodDef, uint>();
            for (var i = 0; i < methodBodies.Count; i++)
            {
                var body = methodBodies[i];
                while (section.Position % 4 != 0) section.WriteByte(0);
                bodyOffsets.Add((int)section.Position - methodStreamOffset);
                methodRvas[methods[i]] = (uint)(methodStreamRva + section.Position - methodStreamOffset);
                WriteFatMethodHeader(section, body);
                section.Write(body.Code, 0, body.Code.Length);
            }

            // 3. 元数据区（4 字节对齐）：元数据根 + 流（#~ #Strings #US #GUID #Blob）
            while (section.Position % 4 != 0) section.WriteByte(0);
            var metadataOffset = (int)section.Position;
            var metadataRva = (uint)(textRva + metadataOffset);

            var blobs = metadata.Serialize(methodRvas);

            var streams = new (string Name, byte[] Bytes)[]
            {
                ("#~", blobs.Tables),
                ("#Strings", blobs.Strings),
                ("#US", blobs.Us),
                ("#GUID", blobs.Guid),
                ("#Blob", blobs.Blob),
            };

            WriteMetadataRoot(section, metadataOffset, streams);

            var sectionBytes = section.ToArray();

            // ---- 回填 CLR 头 ----
            WriteCorHeader(sectionBytes, corHeaderOffset, metadataRva, sectionBytes.Length - metadataOffset, entryPointToken);

            // ---- PE 壳 ----
            var directories = new List<(PeDataDirectoryEntry Entry, uint Rva, uint Size)>
            {
                (PeDataDirectoryEntry.ComDescriptor, (uint)corHeaderRva, (uint)CorHeaderSize),
            };

            var sections = new List<PeSectionSpec>
            {
                new PeSectionSpec(".text", sectionBytes, textRva, 0x60000020), // Read|Execute|Code
            };

            // 6d-4：netfx 直接运行（mscoree 导入 + I386/PE32）暂不可用，统一走 netcore AMD64 布局。
            var config = new PeImageConfig(PeMachine.AMD64, 0x140000000UL, 3, 0x8540, 0)
            {
                SectionAlignment = 0x1000,
                FileAlignment = 0x200,
                SizeOfHeaders = 0x400,
            };

            return PeImageBuilder.Build(config, sections, directories);
        }

        private static void WriteFatMethodHeader(Stream section, MethodBodyBlob body)
        {
            ushort flags = 0x3003; // Fat | 头大小 3（12 字节）
            if (body.LocalVarSigToken != 0)
            {
                flags |= 0x10; // InitLocals
            }

            WriteUInt16(section, flags);
            WriteUInt16(section, body.MaxStack);
            WriteInt32(section, body.Code.Length);
            WriteInt32(section, (int)body.LocalVarSigToken);
        }

        private static void WriteMetadataRoot(Stream section, int metadataStartOffset, (string Name, byte[] Bytes)[] streams)
        {
            var versionBytes = Encoding.UTF8.GetBytes(RuntimeVersion + "\0");
            var versionLength = Align4(versionBytes.Length);

            WriteInt32(section, (int)CorHdrSignature); // BSJB
            WriteUInt16(section, (ushort)CorHdrMajorVersion);
            WriteUInt16(section, (ushort)CorHdrMinorVersion);
            WriteInt32(section, 0); // reserved
            WriteInt32(section, versionLength);
            section.Write(versionBytes, 0, versionBytes.Length);
            while (section.Position % 4 != 0) section.WriteByte(0);

            WriteUInt16(section, 0); // Flags
            WriteUInt16(section, (ushort)streams.Length);

            // 流头：offset 相对元数据根起点（BSJB 处）。
            var headerOffsets = new List<int>();
            for (var i = 0; i < streams.Length; i++)
            {
                headerOffsets.Add((int)section.Position);
                WriteInt32(section, 0); // offset 占位
                var size = Align4(streams[i].Bytes.Length);
                WriteInt32(section, size);
                var nameBytes = Encoding.ASCII.GetBytes(streams[i].Name + "\0");
                section.Write(nameBytes, 0, nameBytes.Length);
                while (section.Position % 4 != 0) section.WriteByte(0);
            }

            // 回填流偏移（首个流紧跟流头区）
            var firstStreamOffset = (int)section.Position - metadataStartOffset;
            var current = section.Position;
            for (var i = 0; i < streams.Length; i++)
            {
                section.Position = headerOffsets[i];
                WriteInt32(section, firstStreamOffset + StreamOffset(streams, i));
            }

            section.Position = current;

            // 流数据
            foreach (var (_, bytes) in streams)
            {
                section.Write(bytes, 0, bytes.Length);
                while (section.Position % 4 != 0) section.WriteByte(0);
            }
        }

        private static int StreamOffset((string Name, byte[] Bytes)[] streams, int index)
        {
            var offset = 0;
            for (var i = 0; i < index; i++)
            {
                offset += Align4(streams[i].Bytes.Length);
            }

            return offset;
        }

        private static void WriteCorHeader(byte[] section, int offset, uint metadataRva, int metadataSize, uint entryPointToken)
        {
            using var stream = new MemoryStream(section);
            stream.Position = offset;
            var writer = new BinaryWriter(stream);

            writer.Write((uint)CorHeaderSize);     // cb
            writer.Write((ushort)2);               // MajorRuntimeVersion
            writer.Write((ushort)5);               // MinorRuntimeVersion
            writer.Write(metadataRva);             // MetaData RVA
            writer.Write((uint)metadataSize);      // MetaData Size
            writer.Write((uint)1);                 // Flags: COMIMAGE_FLAGS_ILONLY
            writer.Write(entryPointToken);         // EntryPointToken
            writer.Write((uint)0);                 // Resources RVA
            writer.Write((uint)0);                 // Resources Size
            writer.Write((uint)0);                 // StrongNameSignature RVA
            writer.Write((uint)0);                 // StrongNameSignature Size
            writer.Write((uint)0);                 // CodeManagerTable
            writer.Write((uint)0);                 // VTableFixups
            writer.Write((uint)0);                 // ExportAddressTableJumps
            writer.Write((uint)0);                 // ManagedNativeHeader
        }

        private static int Align4(int value) => (value + 3) & ~3;

        private static void WriteUInt16(Stream stream, ushort value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
        }

        private static void WriteInt32(Stream stream, int value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 24));
        }
    }
}
