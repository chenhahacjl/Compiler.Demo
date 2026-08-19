using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Cocoa.CodeAnalysis.Emit.IL;
using Xunit;

namespace Cocoa.Tests.CodeAnalysis.Emit.IL
{
    public class IlPInvokeTests
    {
        private static byte[] BuildPe(params IlMethodDef[] methods)
        {
            var metadata = new MetadataBuilder("test", "test");
            var bodies = new List<ManagedPEWriter.MethodBodyBlob>();
            var withBodies = new List<IlMethodDef>();

            foreach (var method in methods)
            {
                metadata.AddMethodDef(method);
                if (method.Body != null)
                {
                    withBodies.Add(method);
                    bodies.Add(new ManagedPEWriter.MethodBodyBlob(new byte[] { 0x2A /* ret */ }, 0, 8));
                }
            }

            return ManagedPEWriter.Build("test", withBodies, bodies, metadata, 0, IlTarget.Default);
        }

        private static byte[] GetMetadataRoot(byte[] pe)
        {
            using var peReader = new PEReader(new MemoryStream(pe));
            return System.Linq.Enumerable.ToArray(peReader.GetMetadata().GetContent());
        }

        private static System.Reflection.Metadata.MetadataReader CreateMetadataReader(byte[] pe)
        {
            using var peReader = new PEReader(new MemoryStream(pe));
            var provider = MetadataReaderProvider.FromMetadataImage(peReader.GetMetadata().GetContent());
            return provider.GetMetadataReader();
        }

        // ------------------------------------------------------------------
        // PEReader side: MethodDef rows (PInvokeImpl flag / RVA / signature convention)
        // ------------------------------------------------------------------

        [Fact]
        public void PInvoke_Methods_Have_PInvokeImpl_Flag_And_Zero_Rva()
        {
            var getTickCount = new IlMethodDef("GetTickCount", IlType.Int32, Array.Empty<IlType>(), null, "kernel32.dll", null, IlCallingConvention.StdCall);
            var normal = new IlMethodDef("Foo", IlType.Int32, Array.Empty<IlType>(), new IlMethodBody(new List<IlInstruction>(), Array.Empty<IlType>(), 0));

            var pe = BuildPe(normal, getTickCount);
            var md = CreateMetadataReader(pe);

            var normalDef = md.GetMethodDefinition(FindMethodHandle(md, "Foo"));
            var tickDef = md.GetMethodDefinition(FindMethodHandle(md, "GetTickCount"));

            Assert.Equal(0u, (uint)normalDef.Attributes & 0x2000u); // normal method has no PInvokeImpl
            Assert.NotEqual(0, normalDef.RelativeVirtualAddress);

            Assert.Equal(0x2000u, (uint)tickDef.Attributes & 0x2000u);
            Assert.Equal(0, tickDef.RelativeVirtualAddress);

            // extern 方法需要 PreserveSig（ImplFlags），否则 JIT 桩对返回值的处理不同
            Assert.Equal(System.Reflection.MethodImplAttributes.PreserveSig, tickDef.ImplAttributes);
            Assert.Equal(System.Reflection.MethodImplAttributes.IL, normalDef.ImplAttributes);

            // extern 方法签名必须用默认调用约定（0x00），本机约定由 ImplMap.MappingFlags 表达
            Assert.Equal(0x00, md.GetBlobBytes(tickDef.Signature)[0] & 0x0F);
            Assert.Equal(0x00, md.GetBlobBytes(normalDef.Signature)[0] & 0x0F);
        }

        [Fact]
        public void EmitPipeline_Produces_Valid_ImplMap_Rows()
        {
            var source = @"
import kernel32.dll

stdcall function GetCurrentProcessId(): int

function main()
{
    var t = GetCurrentProcessId()
    print(t)
}";
            var syntaxTree = Cocoa.CodeAnalysis.Syntax.SyntaxTree.Parse(source);
            var compilation = Cocoa.CodeAnalysis.Compilation.Create(syntaxTree);
            var exePath = Path.Combine(Path.GetTempPath(), "cocoa-il-tests", "pinvoke-pipeline-" + Guid.NewGuid().ToString("N") + ".exe");
            var diagnostics = compilation.Emit("test", new[] { typeof(object).Assembly.Location, typeof(System.Console).Assembly.Location }, exePath);

            Assert.Empty(diagnostics);

            var root = GetMetadataRoot(File.ReadAllBytes(exePath));
            var tables = ReadTableStream(root);
            var strings = ReadStringsStream(root);

            Assert.Equal(1, RowCount(tables, (int)TableId.ModuleRef));
            Assert.Equal(1, RowCount(tables, (int)TableId.ImplMap));

            var implMapOffset = TableOffset(tables, (int)TableId.ImplMap);
            var impl = ReadImplMap(tables, strings, implMapOffset);

            // 方法行号与 MethodDef 表一致（ImmutableDictionary 迭代顺次不保证，故由表内动态推导）
            var methodDefRow = FindMethodRow(tables, strings, "GetCurrentProcessId");

            Assert.Equal(methodDefRow, impl.MemberForwarded >> 1);
            Assert.Equal(0x0302, impl.MappingFlags);
            Assert.Equal("GetCurrentProcessId", impl.ImportName);
            Assert.Equal(1, impl.ImportScope);

            var moduleRefOffset = TableOffset(tables, (int)TableId.ModuleRef);
            Assert.Equal("kernel32.dll", ReadModuleRefName(tables, strings, moduleRefOffset));
        }

        private static MethodDefinitionHandle FindMethodHandle(System.Reflection.Metadata.MetadataReader md, string name) =>
            md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == name);

        // 在 MethodDef 表中按名字找 1-based 行号；行 = RVA(4)+ImplFlags(2)+Flags(2)+Name(#Strings)
        private static int FindMethodRow(byte[] tables, byte[] strings, string name)
        {
            var stringIsBig = (tables[4] & 0x01) != 0;
            var rowCount = RowCount(tables, (int)TableId.MethodDef);
            var offset = TableOffset(tables, (int)TableId.MethodDef);

            for (var row = 0; row < rowCount; row++)
            {
                var nameOffset = stringIsBig
                    ? BitConverter.ToInt32(tables, offset + 8)
                    : BitConverter.ToUInt16(tables, offset + 8);
                if (ReadStringHeap(strings, nameOffset) == name)
                {
                    return row + 1;
                }

                offset += 14;
            }

            throw new Exception("method not found: " + name);
        }

        // ------------------------------------------------------------------
        // Raw table-stream walk: ModuleRef / ImplMap rows
        // (this .NET 9 MetadataReader does not expose the ImplMap table)
        // ------------------------------------------------------------------

        private enum TableId
        {
            Module = 0x00,
            TypeRef = 0x01,
            TypeDef = 0x02,
            MethodDef = 0x06,
            Param = 0x08,
            MemberRef = 0x0A,
            CustomAttribute = 0x0C,
            StandAloneSig = 0x11,
            ModuleRef = 0x1A,
            ImplMap = 0x1C,
            Assembly = 0x20,
            AssemblyRef = 0x23,
        }

        [Fact]
        public void TableStream_Has_ModuleRef_And_ImplMap_Tables_With_Valid_Bits()
        {
            var a = new IlMethodDef("A", IlType.Int32, Array.Empty<IlType>(), null, "kernel32.dll", null, IlCallingConvention.StdCall);
            var normal = new IlMethodDef("Foo", IlType.Int32, Array.Empty<IlType>(), new IlMethodBody(new List<IlInstruction>(), Array.Empty<IlType>(), 0));

            var tables = ReadTableStream(GetMetadataRoot(BuildPe(normal, a)));

            var valid = BitConverter.ToUInt64(tables, 8);
            var sorted = BitConverter.ToUInt64(tables, 16);

            Assert.NotEqual(0UL, valid & (1UL << 0x1A)); // ModuleRef
            Assert.NotEqual(0UL, valid & (1UL << 0x1C)); // ImplMap
            Assert.NotEqual(0UL, sorted & (1UL << 0x1C)); // ImplMap must be sorted
        }

        [Fact]
        public void ModuleRef_And_ImplMap_Rows_Are_Correct()
        {
            var a = new IlMethodDef("A", IlType.Int32, Array.Empty<IlType>(), null, "kernel32.dll", null, IlCallingConvention.StdCall);
            var b = new IlMethodDef("B", IlType.Int32, Array.Empty<IlType>(), null, "kernel32.dll", null, IlCallingConvention.Cdecl);
            var c = new IlMethodDef("C", IlType.Int32, Array.Empty<IlType>(), null, "user32.dll", null, IlCallingConvention.Winapi);
            var normal = new IlMethodDef("Foo", IlType.Int32, Array.Empty<IlType>(), new IlMethodBody(new List<IlInstruction>(), Array.Empty<IlType>(), 0));

            var root = GetMetadataRoot(BuildPe(normal, a, b, c));
            var tables = ReadTableStream(root);
            var strings = ReadStringsStream(root);

            Assert.Equal(2, RowCount(tables, (int)TableId.ModuleRef));
            Assert.Equal(3, RowCount(tables, (int)TableId.ImplMap));

            // ModuleRef rows: deduplicated, first-seen order
            var moduleRefOffset = TableOffset(tables, (int)TableId.ModuleRef);
            Assert.Equal("kernel32.dll", ReadModuleRefName(tables, strings, moduleRefOffset));
            Assert.Equal("user32.dll", ReadModuleRefName(tables, strings, moduleRefOffset + 2));

            // ImplMap rows: sorted by MemberForwarded; MemberForwarded = (rowId << 1) | 1
            var implMapOffset = TableOffset(tables, (int)TableId.ImplMap);

            var aImpl = ReadImplMap(tables, strings, implMapOffset);
            Assert.Equal(2, aImpl.MemberForwarded >> 1); // method row 2 = A
            Assert.Equal(0x0302, aImpl.MappingFlags);    // StdCall | CharSetAnsi
            Assert.Equal("A", aImpl.ImportName);
            Assert.Equal(1, aImpl.ImportScope);          // first ModuleRef = kernel32.dll

            var bImpl = ReadImplMap(tables, strings, implMapOffset + 8);
            Assert.Equal(3, bImpl.MemberForwarded >> 1); // B
            Assert.Equal(0x0202, bImpl.MappingFlags);    // Cdecl | CharSetAnsi
            Assert.Equal("B", bImpl.ImportName);
            Assert.Equal(1, bImpl.ImportScope);

            var cImpl = ReadImplMap(tables, strings, implMapOffset + 16);
            Assert.Equal(4, cImpl.MemberForwarded >> 1); // C
            Assert.Equal(0x0102, cImpl.MappingFlags);    // Winapi | CharSetAnsi
            Assert.Equal("C", cImpl.ImportName);
            Assert.Equal(2, cImpl.ImportScope);          // user32.dll
        }

        [Fact]
        public void EncodeMethodSignature_Uses_Default_CallingConvention()
        {
            var builder = new MetadataBuilder("test", "test");

            // extern 方法的签名约定固定为默认（0x00），本机约定由 ImplMap.MappingFlags 表达
            var signature = builder.EncodeMethodSignature(IlType.Int32, Array.Empty<IlType>(), isStatic: true);

            Assert.Equal(0x00, signature[0]);
            Assert.Equal(0x00, signature[0] & 0x0F);
        }

        // ------------------------------------------------------------------
        // Metadata root + #~ stream parsing helpers
        // ------------------------------------------------------------------

        // HEADER: reserved(4) major(1) minor(1) heapSizes(1) reserved(1) valid(8) sorted(8) rowCounts...
        private static int RowCount(byte[] tables, int tableId)
        {
            var valid = BitConverter.ToUInt64(tables, 8);
            var offset = 24;
            foreach (var id in Enumerable.Range(0, 64).Where(i => (valid & (1UL << i)) != 0))
            {
                if (id == tableId) return BitConverter.ToInt32(tables, offset);
                offset += 4;
            }

            return 0;
        }

        private static int TableOffset(byte[] tables, int tableId)
        {
            var valid = BitConverter.ToUInt64(tables, 8);
            var cursor = 24;
            var headerSize = 24;

            foreach (var id in Enumerable.Range(0, 64).Where(i => (valid & (1UL << i)) != 0))
            {
                cursor += 4;
                headerSize += 4;
            }

            if ((valid & (1UL << tableId)) == 0) throw new Exception("table missing: " + tableId);

            // walk rows of all tables in ascending id order until tableId
            cursor = headerSize;
            foreach (var id in Enumerable.Range(0, 64).Where(i => (valid & (1UL << i)) != 0))
            {
                var count = RowCount(tables, id);
                if (id == tableId) return cursor;
                for (var r = 0; r < count; r++) cursor += RowSize(id, tables[4]);
            }

            throw new Exception("unreachable");
        }

        private static int RowSize(int tableId, byte heapSizes)
        {
            var blobIsBig = (heapSizes & 0x04) != 0;
            var stringIsBig = (heapSizes & 0x01) != 0;

            switch (tableId)
            {
                case 0x00: return 10; // Module: Gen(2) Name(2) Mvid(2) EncId(2) EncBaseId(2)
                case 0x01: return stringIsBig ? 8 : 6;  // TypeRef: Scope(2) Name(2) Namespace(2)
                case 0x02: return 14; // TypeDef: Flags(4) Name(2) Namespace(2) Extends(2) FieldList(2) MethodList(2)
                case 0x04: return 6;  // Field: Flags(2) Name(2) Signature(2)
                case 0x06: return 14; // MethodDef: RVA(4) ImplFlags(2) Flags(2) Name(2) Signature(2) ParamList(2)
                case 0x08: return 6;  // Param: Flags(2) Seq(2) Name(2)
                case 0x0A: return 6;  // MemberRef: Class(2) Name(2) Signature(2)
                case 0x0C: return 6;  // CustomAttribute: Parent(2) Type(2) Value(2)
                case 0x11: return blobIsBig ? 4 : 2;  // StandAloneSig: Signature(#Blob)
                case 0x14: return 6;  // Event: Flags(2) Name(2) EventType(2)
                case 0x17: return 6;  // Property: Flags(2) Name(2) Type(2)
                case 0x1A: return stringIsBig ? 4 : 2;  // ModuleRef: Name(#Strings)

                case 0x1C: return 8;  // ImplMap: MappingFlags(2) MemberForwarded(2) ImportName(2) ImportScope(2)
                default: throw new Exception("row size not implemented: " + tableId);
            }
        }

        private static string ReadStringHeap(byte[] strings, int offset)
        {
            var end = offset;
            while (end < strings.Length && strings[end] != 0) end++;
            return System.Text.Encoding.UTF8.GetString(strings, offset, end - offset);
        }

        private static string ReadModuleRefName(byte[] tables, byte[] strings, int rowOffset) =>
            ReadStringHeap(strings, BitConverter.ToUInt16(tables, rowOffset));

        private static (ushort MappingFlags, int MemberForwarded, string ImportName, int ImportScope) ReadImplMap(byte[] tables, byte[] strings, int rowOffset)
        {
            var flags = BitConverter.ToUInt16(tables, rowOffset);
            var memberForwarded = BitConverter.ToUInt16(tables, rowOffset + 2);
            var importName = BitConverter.ToUInt16(tables, rowOffset + 4);
            var importScope = BitConverter.ToUInt16(tables, rowOffset + 6);
            return (flags, memberForwarded, ReadStringHeap(strings, importName), importScope);
        }

        // metadata root: BSJB(4) major(2) minor(2) reserved(4) versionLen(4) version
        // + flags(2) streams(2) + streamHeaders (offset(4) size(4) name, aligned to 4)
        private static (int Offset, int Size) FindStream(byte[] root, string streamName)
        {
            var versionLen = BitConverter.ToInt32(root, 12);
            var streamCount = BitConverter.ToUInt16(root, 18 + versionLen); // flags(2) then streams(2)
            var cursor = 20 + versionLen;

            for (var i = 0; i < streamCount; i++)
            {
                var nameOffset = cursor + 8;
                var nameEnd = nameOffset;
                while (nameEnd < root.Length && root[nameEnd] != 0) nameEnd++;
                var name = System.Text.Encoding.ASCII.GetString(root, nameOffset, nameEnd - nameOffset);
                var size = BitConverter.ToInt32(root, cursor + 4);
                var offset = BitConverter.ToInt32(root, cursor);

                if (name == streamName) return (offset, size);

                cursor += 8 + ((nameEnd - nameOffset + 3) & ~3);
            }

            throw new Exception("stream not found: " + streamName
                + " versionLen=" + BitConverter.ToInt32(root, 12)
                + " streamCount=" + BitConverter.ToUInt16(root, 16 + BitConverter.ToInt32(root, 12))
                + " head=" + BitConverter.ToString(root.AsSpan(0, Math.Min(64, root.Length)).ToArray()));
        }

        private static byte[] ReadTableStream(byte[] root)
        {
            var (offset, size) = FindStream(root, "#~");
            return root.AsSpan(offset, size).ToArray();
        }

        private static byte[] ReadStringsStream(byte[] root)
        {
            var (offset, size) = FindStream(root, "#Strings");
            return root.AsSpan(offset, size).ToArray();
        }
    }
}