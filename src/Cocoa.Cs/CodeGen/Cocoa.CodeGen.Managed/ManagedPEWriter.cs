using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Cocoa.CodeGen.ObjectFormat.PE;
using static Cocoa.CodeGen.ObjectFormat.PE.PeBinary;

using Cocoa.IlMetadata;
using Cocoa.CodeAnalysis;
using Cocoa.Targeting;

namespace Cocoa.CodeGen.Managed
{
    /// <summary>
    /// 托管 PE 写入器：组装 .text 段（CLR �?+ 方法�?+ 元数据根 + 流）+ IMAGE_COR20_HEADER + PE 壳�?
    /// 布局参�?Roslyn ManagedPEBuilder / System.Reflection.Metadata.Ecma335 的序列化顺序�?
    /// </summary>
    public static class ManagedPEWriter
    {
        private const uint CorHdrSignature = 0x424A5342; // "BSJB"
        private const uint CorHdrMajorVersion = 1;
        private const uint CorHdrMinorVersion = 1;
        private const string RuntimeVersion = "v4.0.30319";

        public const uint TextRva = 0x1000;
        private const int CorHeaderSize = 72; // IMAGE_COR20_HEADER

        // 1c/C8：netfx 镜像基址唯一事实来源——入�?stub 的绝对操作数（stubTarget）与
        // PeImageConfig �?imageBase 必须同源，旧实现两处硬编�?0x400000 构成双写隐患
        // （改一处漏一处时�?reloc �?HIGHLOW 会在错误基址上叠 delta，偏移恰好差基址差值）
        private const ulong NetFxImageBase = 0x400000;

        /// <summary>方法体（已编码字�?+ 局部变量签�?token + 最大栈 + 可选异常表）�?/summary>
        public sealed class MethodBodyBlob
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
        /// 生成托管 exe�?
        /// </summary>
        /// <param name="moduleName">模块名（不含扩展名）�?/param>
        /// <param name="methods">方法定义（顺序与 <paramref name="methodBodies"/> 一致）�?/param>
        /// <param name="methodBodies">每个方法的编码后方法体�?/param>
        /// <param name="metadata">元数据构建器（已收集全部引用/方法）�?/param>
        /// <param name="entryPointToken">入口�?MethodDef token�?/param>
        /// <param name="target">目标运行时：netfx 追加 mscoree.dll!CorExeMain 导入表（Windows 直接激�?CLR），netcore 无导入�?/param>
        public static byte[] Build(
            string moduleName,
            IReadOnlyList<IlMethodDef> methods,
            IReadOnlyList<MethodBodyBlob> methodBodies,
            MetadataBuilder metadata,
            uint entryPointToken,
            IlTarget target,
            ResourceSectionResult? win32Resources = null,
            (Guid PdbGuid, string PdbPath)? portablePdb = null)
        {
            // 6d-4：netfx（I386/PE32，AnyCPU）用 csc 同款节对�?0x2000�?text 起始 0x2000�?
            // netcore（AMD64）保�?0x1000/.text 0x1000�?
            var textRva = target.IsNetFx ? 0x2000u : TextRva;

            // ---- .text 布局 ----
            var section = new MemoryStream();

            // netfx：IAT 槽数组前�?.text（RVA 0x2000，csc 同款——描述符 FirstThunk 指向此处）�?
            var iatOffsetInText = -1;
            var iatSlotRva = 0u;
            if (target.IsNetFx)
            {
                iatOffsetInText = (int)section.Position;
                iatSlotRva = (uint)(textRva + iatOffsetInText);
                section.Write(new byte[2 * ImageThunkData32.SizeOfEntry]); // 1 �?+ null 终止
            }

            // 1. CLR 头（72 字节，放�?.text 开头；netfx �?IAT 之后�?
            var corHeaderOffset = (int)section.Position;
            var corHeaderRva = (uint)(textRva + corHeaderOffset);
            section.Write(new byte[CorHeaderSize]);

            // 2. 方法体区�? 字节对齐；Fat �?12 字节 + 代码�?
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

            // 3. 元数据区�? 字节对齐）：元数据根 + 流（#~ #Strings #US #GUID #Blob�?
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

            // ---- 内嵌资源目录（CLR Resources；ManifestResource.Offset 相对此起点，8 字节对齐�?---
            var resourcesBlob = blobs.Resources;
            while (section.Position % 8 != 0) section.WriteByte(0);
            var resourcesOffsetInText = (int)section.Position;
            var resourcesRva = (uint)(textRva + resourcesOffsetInText);
            if (resourcesBlob.Length > 0)
            {
                section.Write(resourcesBlob, 0, resourcesBlob.Length);
            }

            var metadataSize = resourcesOffsetInText - metadataOffset;
            var sectionBytes = section.ToArray();

            // ---- 回填 CLR 头（�?Resources 目录 RVA/Size；COR20 Flags �?Cpu/Prefer32Bit�?---
            WriteCorHeader(sectionBytes, corHeaderOffset, metadataRva, metadataSize, entryPointToken,
                resourcesBlob.Length > 0 ? resourcesRva : 0u, (uint)resourcesBlob.Length, target);

            // ---- PE �?----
            var directories = new List<(PeDataDirectoryEntry Entry, uint Rva, uint Size)>
            {
                (PeDataDirectoryEntry.ComDescriptor, (uint)corHeaderRva, (uint)CorHeaderSize),
            };

            var sections = new List<PeSectionSpec>
            {
                new PeSectionSpec(".text", sectionBytes, textRva, 0x60000020), // Read|Execute|Code
            };

            // PE 形态（PE32/PE32+）由 Cpu 决定（AnyCPU/x86 �?PE32；x64 �?PE32+），
            // �?netfx/netcore 布局（mscoree 宿主）正交——netfx 固定 AnyCPU/x86（PE32），
            // netcore 默认 AnyCPU（PE32），x64 显式时才 PE32+�?
            var pe32 = target.IsPe32;
            var isNetFxLayout = target.IsNetFx;
            var addressOfEntryPoint = 0u;
            if (isNetFxLayout)
            {
                // netfx：导入表（描述符+INT+hint/name）内�?.text（csc 同款），IAT 槽前�?.text（外�?IAT）�?
                var blobBaseRva = (uint)(textRva + sectionBytes.Length);
                var importLayout = BuildMscoreeImport(blobBaseRva, iatSlotRva);

                // 入口 stub：FF 25 <u32> = jmp dword ptr [u32]（跳 IAT 槽，加载器已�?_CorExeMain�?
                var stubTarget = (uint)(NetFxImageBase + iatSlotRva);
                var stub = new byte[] { 0xFF, 0x25,
                    (byte)stubTarget, (byte)(stubTarget >> 8), (byte)(stubTarget >> 16), (byte)(stubTarget >> 24) };

                var newText = new byte[sectionBytes.Length + importLayout.Blob.Length + stub.Length];
                Array.Copy(sectionBytes, newText, sectionBytes.Length);
                Array.Copy(importLayout.Blob, 0, newText, sectionBytes.Length, importLayout.Blob.Length);
                Array.Copy(stub, 0, newText, sectionBytes.Length + importLayout.Blob.Length, stub.Length);
                sections[0] = new PeSectionSpec(".text", newText, textRva, 0x60000020);
                addressOfEntryPoint = (uint)(blobBaseRva + importLayout.Blob.Length);

                // IAT 槽磁盘初�?= hint/name RVA�?c-2 fake-IAT：槽不能留零�?
                var iatSlotValue = blobBaseRva + importLayout.HintNameRva;
                WriteUInt32(newText, iatOffsetInText, (int)iatSlotValue);

                directories.Add((PeDataDirectoryEntry.Import, importLayout.ImportRva, importLayout.ImportSize));
                directories.Add((PeDataDirectoryEntry.Iat, iatSlotRva, (uint)(2 * ImageThunkData32.SizeOfEntry)));

                // .reloc：stub �?jmp 操作数（64 �?AnyCPU 进程 FF 25 �?RIP 相对解码）需 HIGHLOW 重定�?
                AppendReloc(sections, directories, addressOfEntryPoint + 2);
            }

            // ---- .rsrc（win32 图标 / 通用 RT_RCDATA）：紧随既有节布局，DataEntry.OffsetToData 回填�?RVA ----
            if (win32Resources != null && win32Resources.Blob.Length > 0)
            {
                var sectionAlignment = pe32 ? 0x2000u : 0x1000u;
                var lastEnd = 0u;
                foreach (var sect in sections)
                {
                    var end = sect.VirtualAddress + (uint)sect.RawData.Length;
                    if (end > lastEnd) lastEnd = end;
                }

                var rsrcRva = PeBinary.Align(lastEnd, sectionAlignment);
                var resourceBlob = (byte[])win32Resources.Blob.Clone();
                foreach (var dataEntryOffset in win32Resources.DataEntryOffsets)
                {
                    var dataOffsetInBlob = (int)PeBinary.ReadUInt32(resourceBlob, dataEntryOffset);
                    PeBinary.WriteUInt32(resourceBlob, dataEntryOffset, (int)(rsrcRva + (uint)dataOffsetInBlob));
                }

                directories.Add((PeDataDirectoryEntry.Resource, rsrcRva, (uint)resourceBlob.Length));
                sections.Add(new PeSectionSpec(".rsrc", resourceBlob, rsrcRva, PeSectionCharacteristics.Data));
            }

            PeImageConfig config;
            if (isNetFxLayout)
            {
                // 6d-4 netfx：csc 同款布局（节对齐 0x2000、OS/子系�?4.0、SizeOfHeaders 0x200）；
                // ASLR�?x8540）开�?reloc 节已提供重定位�?
                config = new PeImageConfig(PeMachine.I386, NetFxImageBase, 3, 0x8540, addressOfEntryPoint)
                {
                    SectionAlignment = 0x2000,
                    FileAlignment = 0x200,
                    SizeOfHeaders = 0x200,
                    MajorOperatingSystemVersion = 4,
                    MinorOperatingSystemVersion = 0,
                    MajorSubsystemVersion = 4,
                    MinorSubsystemVersion = 0,
                    FileCharacteristicsOverride = (ushort)(PeFileCharacteristics.ExecutableImage | PeFileCharacteristics.Machine32Bit),
                };
            }
            else if (pe32)
            {
                // netcore + AnyCPU/x86：PE32（I386），标准 netcore 布局
                config = new PeImageConfig(PeMachine.I386, 0x400000UL, 3, 0x8540, 0)
                {
                    SectionAlignment = 0x1000,
                    FileAlignment = 0x200,
                    SizeOfHeaders = 0x400,
                    FileCharacteristicsOverride = (ushort)(PeFileCharacteristics.ExecutableImage | PeFileCharacteristics.Machine32Bit),
                };
            }
            else
            {
                // netcore + x64：PE32+
                config = new PeImageConfig(PeMachine.AMD64, 0x140000000UL, 3, 0x8540, 0)
                {
                    SectionAlignment = 0x1000,
                    FileAlignment = 0x200,
                    SizeOfHeaders = 0x400,
                };
            }

            // Portable PDB 匹配�?text 末尾追加 CodeView 调试目录 + RSDS（GUID = PDB �?debug metadata id�?
            // VS/dotnet 靠它自动找到旁置 .pdb 并加载符号）。追加在方法�?元数据之后，不改既有布局�?
            if (portablePdb != null) {
                var text = sections[0];
                var debugDirRva = text.VirtualAddress + (uint)text.RawData.Length;
                var rsdsRva = debugDirRva + (uint)ImageDebugDirectory.SizeOfEntry;
                var pdbPathBytes = System.Text.Encoding.UTF8.GetBytes(System.IO.Path.GetFileName(portablePdb.Value.PdbPath));
                var rsds = new PeCodeViewRsds(PeCodeViewRsds.RsdsSignature, portablePdb.Value.PdbGuid, 1, pdbPathBytes);
                var rsdsBlob = new byte[rsds.Size];
                rsds.Write(rsdsBlob);
                var debugDir = new ImageDebugDirectory(
                    Characteristics: 0,
                    TimeDateStamp: 0,
                    MajorVersion: 0,
                    MinorVersion: 0,
                    Type: PeDebugType.CodeView,
                    SizeOfData: (uint)rsdsBlob.Length,
                    AddressOfRawData: rsdsRva,
                    PointerToRawData: (uint)(config.SizeOfHeaders + (rsdsRva - text.VirtualAddress)));
                var debugDirBlob = new byte[ImageDebugDirectory.SizeOfEntry];
                debugDir.Write(debugDirBlob);

                var debugBlob = new byte[ImageDebugDirectory.SizeOfEntry + rsdsBlob.Length];
                Array.Copy(debugDirBlob, debugBlob, ImageDebugDirectory.SizeOfEntry);
                Array.Copy(rsdsBlob, 0, debugBlob, ImageDebugDirectory.SizeOfEntry, rsdsBlob.Length);

                var newText = new byte[text.RawData.Length + debugBlob.Length];
                Array.Copy(text.RawData, newText, text.RawData.Length);
                Array.Copy(debugBlob, 0, newText, text.RawData.Length, debugBlob.Length);
                sections[0] = new PeSectionSpec(text.Name, newText, text.VirtualAddress, text.Characteristics);
                directories.Add((PeDataDirectoryEntry.Debug, debugDirRva, (uint)ImageDebugDirectory.SizeOfEntry));
            }

            return PeImageBuilder.Build(config, sections, directories, (ushort)(pe32 ? PeFileCharacteristics.Machine32Bit : 0));
        }

        /// <summary>mscoree 导入表布局（外�?IAT：槽数组独立置于 .text 起始）�?/summary>
        private sealed class IlImportLayout
        {
            public byte[] Blob = Array.Empty<byte>();
            public uint ImportRva;
            public uint ImportSize;
            public uint HintNameRva;
        }

        /// <summary>构建 mscoree.dll!_CorExeMain 导入�?blob（描述符 + INT + hint/name；IAT 槽外部）�?/summary>
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

        /// <summary>追加 .reloc 节：HIGHLOW 重定位指�?stub �?jmp 操作数�?/summary>
        private static void AppendReloc(
            List<PeSectionSpec> sections,
            List<(PeDataDirectoryEntry Entry, uint Rva, uint Size)> directories,
            uint operandRva)
        {
            const uint sectionAlignment = 0x2000;
            // .reloc 放在所有现有节�?text/.idata）的虚拟末端之后，避免节重叠
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
            PeBinary.WriteUInt16(block, 8, (ushort)(((int)PeRelocType.HighLow << 12) | offsetInPage));
            PeBinary.WriteUInt16(block, 10, 0); // ABS 终止

            directories.Add((PeDataDirectoryEntry.BaseReloc, relocRva, 12));
            // .reloc 必须�?Discardable|Read|InitData�?x42000040，不可写）：
            // CLR 4.8 对可写（0xC0000040）的 .reloc 节镜像报 0x80131018 not an assembly manifest�?
            sections.Add(new PeSectionSpec(".reloc", block, relocRva, 0x42000040));
        }

        private static void WriteFatMethodHeader(Stream section, MethodBodyBlob body)
        {
            ushort flags = 0x3003; // Fat | 头大�?3�?2 字节�?
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

            // 流头：offset 相对元数据根起点（BSJB 处）�?
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

            // 回填流偏移（首个流紧跟流头区�?
            var firstStreamOffset = (int)section.Position - metadataStartOffset;
            var current = section.Position;
            for (var i = 0; i < streams.Length; i++)
            {
                section.Position = headerOffsets[i];
                WriteInt32(section, firstStreamOffset + StreamOffset(streams, i));
            }

            section.Position = current;

            // 流数�?
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

        private static void WriteCorHeader(byte[] section, int offset, uint metadataRva, int metadataSize, uint entryPointToken, uint resourcesRva, uint resourcesSize, IlTarget target)
        {
            using var stream = new MemoryStream(section);
            stream.Position = offset;
            var writer = new BinaryWriter(stream);

            // COR20 Flags（COMIMAGE_FLAGS）：ILONLY 恒置；x86 �?32BITREQUIRED；Prefer32Bit(anycpu) �?32BITPREFERRED�?
            const uint CorIlOnly = 0x0001;
            const uint Cor32BitRequired = 0x0002;
            const uint Cor32BitPreferred = 0x00020000;
            var flags = CorIlOnly;
            if (target.Arch == Architecture.X86) flags |= Cor32BitRequired;
            if (target.Prefer32Bit) flags |= Cor32BitPreferred;

            writer.Write((uint)CorHeaderSize);     // cb
            writer.Write((ushort)2);               // MajorRuntimeVersion
            writer.Write((ushort)5);               // MinorRuntimeVersion
            writer.Write(metadataRva);             // MetaData RVA
            writer.Write((uint)metadataSize);      // MetaData Size
            writer.Write(flags);                   // Flags
            writer.Write(entryPointToken);         // EntryPointToken
            writer.Write(resourcesRva);            // Resources RVA
            writer.Write(resourcesSize);           // Resources Size
            writer.Write((uint)0);                 // StrongNameSignature RVA
            writer.Write((uint)0);                 // StrongNameSignature Size
            writer.Write((uint)0);                 // CodeManagerTable
            writer.Write((uint)0);                 // VTableFixups
            writer.Write((uint)0);                 // ExportAddressTableJumps
            writer.Write((uint)0);                 // ManagedNativeHeader
        }

    }
}
