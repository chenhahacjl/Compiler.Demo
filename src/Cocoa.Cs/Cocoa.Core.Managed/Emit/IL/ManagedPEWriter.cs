using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Cocoa.CodeAnalysis.Emit.Native.PEFile;

using Cocoa.CodeAnalysis.Emit.IL;
namespace Cocoa.CodeAnalysis.Emit.Managed
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

        /// <summary>方法体（已编码字节 + 局部变量签名 token + 最大栈 + 可选异常表）。</summary>
        internal sealed class MethodBodyBlob
        {
            public MethodBodyBlob(byte[] code, uint localVarSigToken, ushort maxStack, byte[]? exceptionTable = null)
            {
                Code = code;
                LocalVarSigToken = localVarSigToken;
                MaxStack = maxStack;
                ExceptionTable = exceptionTable;
            }

            public byte[] Code { get; }
            public uint LocalVarSigToken { get; }
            public ushort MaxStack { get; }
            public byte[]? ExceptionTable { get; }
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
            // 6d-4：netfx（I386/PE32，AnyCPU）用 csc 同款节对齐 0x2000、.text 起始 0x2000；
            // netcore（AMD64）保持 0x1000/.text 0x1000。
            var textRva = target.IsNetFx ? 0x2000u : TextRva;

            // ---- .text 布局 ----
            var section = new MemoryStream();

            // netfx：IAT 槽数组前置 .text（RVA 0x2000，csc 同款——描述符 FirstThunk 指向此处）。
            var iatOffsetInText = -1;
            var iatSlotRva = 0u;
            if (target.IsNetFx)
            {
                iatOffsetInText = (int)section.Position;
                iatSlotRva = (uint)(textRva + iatOffsetInText);
                section.Write(new byte[2 * ImageThunkData32.SizeOfEntry]); // 1 槽 + null 终止
            }

            // 1. CLR 头（72 字节，放在 .text 开头；netfx 在 IAT 之后）
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

                if (body.ExceptionTable != null)
                {
                    while (section.Position % 4 != 0) section.WriteByte(0);
                    section.Write(body.ExceptionTable, 0, body.ExceptionTable.Length);
                }
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

            var pe32 = target.IsNetFx;
            var addressOfEntryPoint = 0u;
            if (target.IsNetFx)
            {
                // netfx：导入表（描述符+INT+hint/name）内嵌 .text（csc 同款），IAT 槽前置 .text（外部 IAT）。
                var blobBaseRva = (uint)(textRva + sectionBytes.Length);
                var importLayout = BuildMscoreeImport(blobBaseRva, iatSlotRva);

                // 入口 stub：FF 25 <u32> = jmp dword ptr [u32]（跳 IAT 槽，加载器已填 _CorExeMain）
                var stubTarget = 0x400000u + iatSlotRva;
                var stub = new byte[] { 0xFF, 0x25,
                    (byte)stubTarget, (byte)(stubTarget >> 8), (byte)(stubTarget >> 16), (byte)(stubTarget >> 24) };

                var newText = new byte[sectionBytes.Length + importLayout.Blob.Length + stub.Length];
                Array.Copy(sectionBytes, newText, sectionBytes.Length);
                Array.Copy(importLayout.Blob, 0, newText, sectionBytes.Length, importLayout.Blob.Length);
                Array.Copy(stub, 0, newText, sectionBytes.Length + importLayout.Blob.Length, stub.Length);
                sections[0] = new PeSectionSpec(".text", newText, textRva, 0x60000020);
                addressOfEntryPoint = (uint)(blobBaseRva + importLayout.Blob.Length);

                // IAT 槽磁盘初值 = hint/name RVA（6c-2 fake-IAT：槽不能留零）
                var iatSlotValue = blobBaseRva + importLayout.HintNameRva;
                WriteUInt32(newText, iatOffsetInText, (int)iatSlotValue);

                directories.Add((PeDataDirectoryEntry.Import, importLayout.ImportRva, importLayout.ImportSize));
                directories.Add((PeDataDirectoryEntry.Iat, iatSlotRva, (uint)(2 * ImageThunkData32.SizeOfEntry)));

                // .reloc：stub 的 jmp 操作数（64 位 AnyCPU 进程 FF 25 按 RIP 相对解码）需 HIGHLOW 重定位
                AppendReloc(sections, directories, addressOfEntryPoint + 2);
            }

            var config = pe32
                // 6d-4 netfx：csc 同款布局（节对齐 0x2000、OS/子系统 4.0、SizeOfHeaders 0x200）；
                // ASLR（0x8540）开，.reloc 节已提供重定位。
                ? new PeImageConfig(PeMachine.I386, 0x400000UL, 3, 0x8540, addressOfEntryPoint)
                {
                    SectionAlignment = 0x2000,
                    FileAlignment = 0x200,
                    SizeOfHeaders = 0x200,
                    MajorOperatingSystemVersion = 4,
                    MinorOperatingSystemVersion = 0,
                    MajorSubsystemVersion = 4,
                    MinorSubsystemVersion = 0,
                    FileCharacteristicsOverride = (ushort)(PeFileCharacteristics.ExecutableImage | PeFileCharacteristics.Machine32Bit),
                }
                : new PeImageConfig(PeMachine.AMD64, 0x140000000UL, 3, 0x8540, 0)
                {
                    SectionAlignment = 0x1000,
                    FileAlignment = 0x200,
                    SizeOfHeaders = 0x400,
                };

            return PeImageBuilder.Build(config, sections, directories, (ushort)(pe32 ? PeFileCharacteristics.Machine32Bit : 0));
        }

        /// <summary>mscoree 导入表布局（外部 IAT：槽数组独立置于 .text 起始）。</summary>
        private sealed class IlImportLayout
        {
            public byte[] Blob = Array.Empty<byte>();
            public uint ImportRva;
            public uint ImportSize;
            public uint HintNameRva;
        }

        /// <summary>构建 mscoree.dll!_CorExeMain 导入表 blob（描述符 + INT + hint/name；IAT 槽外部）。</summary>
        private static IlImportLayout BuildMscoreeImport(uint blobBaseRva, uint externalIatRva)
        {
            var specs = new List<PeImportSpec> { new PeImportSpec("mscoree.dll", "_CorExeMain") };
            var slotOffsets = new List<int> { 0 };
            var layout = ImportTableBuilder.Build(specs, blobBaseRva, pe32: true, slotOffsets, externalIatRva);

            return new IlImportLayout
            {
                Blob = layout.Blob,
                ImportRva = blobBaseRva + (uint)layout.DescriptorsOffset,
                ImportSize = (uint)((layout.Dlls.Count + 1) * ImageImportDescriptor.SizeOfEntry),
                HintNameRva = (uint)layout.Dlls[0].Entries[0].HintNameOffset,
            };
        }

        /// <summary>追加 .reloc 节：HIGHLOW 重定位指向 stub 的 jmp 操作数。</summary>
        private static void AppendReloc(
            List<PeSectionSpec> sections,
            List<(PeDataDirectoryEntry Entry, uint Rva, uint Size)> directories,
            uint operandRva)
        {
            const uint sectionAlignment = 0x2000;
            // .reloc 放在所有现有节（.text/.idata）的虚拟末端之后，避免节重叠
            var lastEnd = 0u;
            foreach (var section in sections)
            {
                var end = section.VirtualAddress + (uint)section.RawData.Length;
                if (end > lastEnd) lastEnd = end;
            }

            var relocRva = (uint)Align((int)lastEnd, (int)sectionAlignment);

            var page = operandRva & ~0xFFFu;
            var offsetInPage = (int)(operandRva & 0xFFF);
            var block = new byte[12];
            WriteUInt32(block, 0, (int)page);
            WriteUInt32(block, 4, 12);
            WriteUInt16(block, 8, (ushort)(((int)PeRelocType.HighLow << 12) | offsetInPage));
            WriteUInt16(block, 10, 0); // ABS 终止

            directories.Add((PeDataDirectoryEntry.BaseReloc, relocRva, 12));
            // .reloc 必须为 Discardable|Read|InitData（0x42000040，不可写）：
            // CLR 4.8 对可写（0xC0000040）的 .reloc 节镜像报 0x80131018 not an assembly manifest。
            sections.Add(new PeSectionSpec(".reloc", block, relocRva, 0x42000040));
        }

        private static void WriteUInt32(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteUInt16(byte[] bytes, int offset, ushort value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
        }

        private static int Align(int value, int alignment)
        {
            var remainder = value % alignment;
            return remainder == 0 ? value : value + alignment - remainder;
        }

        private static void WriteFatMethodHeader(Stream section, MethodBodyBlob body)
        {
            ushort flags = 0x3003; // Fat | 头大小 3（12 字节）
            if (body.LocalVarSigToken != 0)
            {
                flags |= 0x10; // InitLocals
            }

            if (body.ExceptionTable != null)
            {
                flags |= 0x0008; // CorILMethod_MoreSections
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
