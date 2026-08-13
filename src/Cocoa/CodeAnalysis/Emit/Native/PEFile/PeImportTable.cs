using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeAnalysis.Emit.Native.PEFile
{
    /// <summary>IMAGE_IMPORT_DESCRIPTOR — 导入描述符（20 字节）。</summary>
    internal readonly record struct ImageImportDescriptor(
        uint OriginalFirstThunk,
        uint TimeDateStamp,
        uint ForwarderChain,
        uint Name,
        uint FirstThunk)
    {
        public static int SizeOfEntry => 20;

        public bool IsEndOfArray => OriginalFirstThunk == 0 && TimeDateStamp == 0 && ForwarderChain == 0 && Name == 0 && FirstThunk == 0;

        public static ImageImportDescriptor Read(ReadOnlySpan<byte> s)
        {
            return new ImageImportDescriptor(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(12)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(16)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, OriginalFirstThunk);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), TimeDateStamp);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(8), ForwarderChain);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(12), Name);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(16), FirstThunk);
        }
    }

    /// <summary>IMAGE_THUNK_DATA64 — 导入项（8 字节），AddressOfData 四语义由辅助属性区分。</summary>
    internal readonly record struct ImageThunkData64(ulong AddressOfData)
    {
        public static int SizeOfEntry => 8;

        public bool IsNull => AddressOfData == 0;

        public bool IsOrdinal => (AddressOfData & PeConstants.OrdinalFlag64) != 0;

        public ushort OrdinalNumber => unchecked((ushort)(AddressOfData & 0xFFFF));

        public uint AddressOfDataRva => unchecked((uint)(AddressOfData & 0x7FFFFFFF));

        public static ImageThunkData64 Read(ReadOnlySpan<byte> s)
        {
            return new ImageThunkData64(BinaryPrimitives.ReadUInt64LittleEndian(s));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(d, AddressOfData);
        }
    }

    /// <summary>IMAGE_THUNK_DATA32 — 导入项（4 字节），AddressOfData 四语义由辅助属性区分。</summary>
    internal readonly record struct ImageThunkData32(uint AddressOfData)
    {
        public static int SizeOfEntry => 4;

        public bool IsNull => AddressOfData == 0;

        public bool IsOrdinal => (AddressOfData & PeConstants.OrdinalFlag32) != 0;

        public ushort OrdinalNumber => unchecked((ushort)(AddressOfData & 0xFFFF));

        public uint AddressOfDataRva => unchecked((uint)(AddressOfData & 0x7FFFFFFF));

        public static ImageThunkData32 Read(ReadOnlySpan<byte> s)
        {
            return new ImageThunkData32(BinaryPrimitives.ReadUInt32LittleEndian(s));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, AddressOfData);
        }
    }

    /// <summary>IMAGE_IMPORT_BY_NAME — Hint + 以 NUL 结尾的函数名。</summary>
    internal readonly record struct ImageImportByName(ushort Hint, byte[] Name)
    {
        public int Size => 2 + Name.Length + 1;

        public string NameString => Encoding.ASCII.GetString(Name);

        public static ImageImportByName Read(ReadOnlySpan<byte> s)
        {
            var hint = BinaryPrimitives.ReadUInt16LittleEndian(s);
            var name = s.Slice(2);
            var end = name.IndexOf((byte)0);
            if (end < 0)
            {
                end = name.Length;
            }

            return new ImageImportByName(hint, name.Slice(0, end).ToArray());
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d, Hint);
            Name.CopyTo(d.Slice(2));
            d[2 + Name.Length] = 0;
        }
    }

    /// <summary>IMAGE_BOUND_IMPORT_DESCRIPTOR — 绑定导入描述符（16 字节）。</summary>
    internal readonly record struct ImageBoundImportDescriptor(
        uint TimeDateStamp,
        ushort OffsetModuleName,
        ushort NumberOfModuleForwarderRefs)
    {
        public static int SizeOfEntry => 16;

        public static ImageBoundImportDescriptor Read(ReadOnlySpan<byte> s)
        {
            return new ImageBoundImportDescriptor(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(6)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, TimeDateStamp);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(4), OffsetModuleName);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(6), NumberOfModuleForwarderRefs);
        }
    }

    /// <summary>IMAGE_BOUND_FORWARDER_REF — 绑定转发表项（8 字节）。</summary>
    internal readonly record struct ImageBoundForwarderRef(uint TimeDateStamp, ushort OffsetModuleName)
    {
        public static int SizeOfEntry => 8;

        public static ImageBoundForwarderRef Read(ReadOnlySpan<byte> s)
        {
            return new ImageBoundForwarderRef(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(4)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, TimeDateStamp);
            BinaryPrimitives.WriteUInt16LittleEndian(d.Slice(4), OffsetModuleName);
        }
    }

    /// <summary>IMAGE_DELAYLOAD_DESCRIPTOR — 延迟加载描述符（32 字节）。</summary>
    internal readonly record struct ImageDelayLoadDescriptor(
        uint Attributes,
        uint DllNameRva,
        uint ModuleHandleRva,
        uint ImportAddressTableRva,
        uint ImportNameTableRva,
        uint BoundImportAddressTableRva,
        uint UnloadInformationTableRva,
        uint TimeDateStamp)
    {
        public static int SizeOfEntry => 32;

        public static ImageDelayLoadDescriptor Read(ReadOnlySpan<byte> s)
        {
            return new ImageDelayLoadDescriptor(
                BinaryPrimitives.ReadUInt32LittleEndian(s),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(8)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(12)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(16)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(20)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(24)),
                BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(28)));
        }

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d, Attributes);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), DllNameRva);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(8), ModuleHandleRva);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(12), ImportAddressTableRva);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(16), ImportNameTableRva);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(20), BoundImportAddressTableRva);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(24), UnloadInformationTableRva);
            BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(28), TimeDateStamp);
        }
    }

    /// <summary>导入规格：DLL 名 + 函数名（或 ordinal）。Ordinal==0 表示按名字导入。</summary>
    internal readonly record struct PeImportSpec(string DllName, string FunctionName, ushort Ordinal = 0)
    {
        public bool ByName => Ordinal == 0;
    }

    /// <summary>每个 DLL 的导入区块布局（offset 相对 blob 起始）。</summary>
    internal sealed class PeImportDllLayout
    {
        public PeImportDllLayout(string dllName, int descriptorOffset, int intOffset, int iatOffset, int dllNameOffset, IReadOnlyList<(PeImportSpec Spec, int HintNameOffset)> entries)
        {
            DllName = dllName;
            DescriptorOffset = descriptorOffset;
            IntOffset = intOffset;
            IatOffset = iatOffset;
            DllNameOffset = dllNameOffset;
            Entries = entries;
        }

        public string DllName { get; }
        public int DescriptorOffset { get; }
        public int IntOffset { get; }
        public int IatOffset { get; }
        public int DllNameOffset { get; }
        public IReadOnlyList<(PeImportSpec Spec, int HintNameOffset)> Entries { get; }
    }

    /// <summary>ImportTableBuilder 产物：blob + 各 DLL 布局。blob 不含 IAT 槽数据本体。</summary>
    internal sealed class PeImportTableLayout
    {
        public PeImportTableLayout(byte[] blob, int descriptorsOffset, IReadOnlyList<PeImportDllLayout> dlls)
        {
            Blob = blob;
            DescriptorsOffset = descriptorsOffset;
            Dlls = dlls;
        }

        public byte[] Blob { get; }
        public int DescriptorsOffset { get; }
        public IReadOnlyList<PeImportDllLayout> Dlls { get; }
    }

    /// <summary>按 DLL 分组的导入表构建器：DLL 名 / INT / HintName / descriptor 组 + 全零终止。IAT 槽位于镜像内 data 区，由运行期 stub 填充。</summary>
    internal static class ImportTableBuilder
    {
        public static PeImportTableLayout Build(IReadOnlyList<PeImportSpec> specs, uint blobBaseRva = 0, bool pe32 = false)
        {
            var dllNames = new List<string>();
            var byDll = new List<List<PeImportSpec>>();

            foreach (var spec in specs)
            {
                var index = dllNames.IndexOf(spec.DllName);
                if (index < 0)
                {
                    dllNames.Add(spec.DllName);
                    byDll.Add(new List<PeImportSpec>());
                    index = byDll.Count - 1;
                }

                byDll[index].Add(spec);
            }

            var parts = new List<byte>();
            var layouts = new List<PeImportDllLayout>();

            foreach (var dll in dllNames)
            {
                var functions = byDll[dllNames.IndexOf(dll)];

                var dllNameOffset = parts.Count;
                WriteAscii(parts, dll, true);

                var entries = new List<(PeImportSpec Spec, int HintNameOffset)>(functions.Count);
                foreach (var function in functions)
                {
                    AlignParts(parts, 2);
                    var hintNameOffset = parts.Count;
                    WriteUInt16(parts, function.Ordinal);
                    WriteAscii(parts, function.FunctionName, true);
                    entries.Add((function, hintNameOffset));
                }

                AlignParts(parts, pe32 ? 4 : 8);
                var intOffset = parts.Count;
                foreach (var entry in entries)
                {
                    WriteThunk(parts, entry, blobBaseRva, pe32);
                }

                WriteThunk(parts, null, blobBaseRva, pe32);

                var iatOffset = parts.Count;
                foreach (var entry in entries)
                {
                    WriteThunk(parts, entry, blobBaseRva, pe32);
                }

                WriteThunk(parts, null, blobBaseRva, pe32);

                var descriptorOffset = parts.Count;
                WriteUInt32(parts, 0);
                WriteUInt32(parts, 0);
                WriteUInt32(parts, 0);
                WriteUInt32(parts, (int)(blobBaseRva + (uint)dllNameOffset));
                WriteUInt32(parts, 0);

                layouts.Add(new PeImportDllLayout(dll, descriptorOffset, intOffset, iatOffset, dllNameOffset, entries));
            }

            var descriptorsOffset = layouts.Count > 0 ? layouts[0].DescriptorOffset : 0;
            for (var i = 0; i < layouts.Count; i++)
            {
                var layout = layouts[i];
                WriteUInt32(parts, layout.DescriptorOffset, (int)(blobBaseRva + (uint)layout.IntOffset));
                WriteUInt32(parts, layout.DescriptorOffset + 16, (int)(blobBaseRva + (uint)layout.IatOffset));
            }

            parts.AddRange(new byte[ImageImportDescriptor.SizeOfEntry]);
            return new PeImportTableLayout(parts.ToArray(), descriptorsOffset, layouts);
        }

        private static void WriteThunk(List<byte> parts, (PeImportSpec Spec, int HintNameOffset)? entry, uint blobBaseRva, bool pe32)
        {
            if (entry == null)
            {
                if (pe32)
                {
                    WriteUInt32(parts, 0);
                }
                else
                {
                    WriteUInt64(parts, 0);
                }

                return;
            }

            var value = entry.Value.Spec.ByName
                ? blobBaseRva + (uint)entry.Value.HintNameOffset
                : (pe32 ? PeConstants.OrdinalFlag32 : PeConstants.OrdinalFlag64) | (ulong)entry.Value.Spec.Ordinal;

            if (pe32)
            {
                WriteUInt32(parts, (int)value);
            }
            else
            {
                WriteUInt64(parts, value);
            }
        }

        private static void AlignParts(List<byte> parts, int alignment)
        {
            while (parts.Count % alignment != 0)
            {
                parts.Add(0);
            }
        }

        private static void WriteUInt16(List<byte> parts, int value)
        {
            parts.Add((byte)value);
            parts.Add((byte)(value >> 8));
        }

        private static void WriteUInt32(List<byte> parts, int value)
        {
            parts.Add((byte)value);
            parts.Add((byte)(value >> 8));
            parts.Add((byte)(value >> 16));
            parts.Add((byte)(value >> 24));
        }

        private static void WriteUInt64(List<byte> parts, ulong value)
        {
            for (var i = 0; i < 8; i++)
            {
                parts.Add((byte)(value >> (i * 8)));
            }
        }

        private static void WriteAscii(List<byte> parts, string value, bool nullTerminated)
        {
            foreach (var c in value)
            {
                parts.Add((byte)c);
            }

            if (nullTerminated)
            {
                parts.Add(0);
            }
        }

        private static void WriteUInt32(List<byte> parts, int offset, int value)
        {
            parts[offset] = (byte)value;
            parts[offset + 1] = (byte)(value >> 8);
            parts[offset + 2] = (byte)(value >> 16);
            parts[offset + 3] = (byte)(value >> 24);
        }
    }

    /// <summary>导入表读取器：按 image span + 导入目录 RVA 解析 DLL/函数列表（供自检与测试）。</summary>
    internal static class ImportTableReader
    {
        public static IReadOnlyList<(string DllName, IReadOnlyList<(bool ByName, ushort Ordinal, string Name)>)> Read(ReadOnlySpan<byte> image, Func<uint, uint> rvaToOffset)
        {
            var result = new List<(string, IReadOnlyList<(bool, ushort, string)>)>();
            if (image.Length < ImageNtHeaders64.Size + 8 * ImageDataDirectory.SizeOfEntry)
            {
                return result;
            }

            uint importRva = 0;
            uint importSize = 0;
            var headers = ImageNtHeaders64.Read(image.Slice(0, ImageNtHeaders64.Size));
            if (headers.Signature != PeConstants.NtSignature || headers.OptionalHeader.Magic != PeOptionalMagic.Pe32Plus)
            {
                return result;
            }

            importRva = headers.OptionalHeader.DataDirectories[(int)PeDataDirectoryEntry.Import].VirtualAddress;
            importSize = headers.OptionalHeader.DataDirectories[(int)PeDataDirectoryEntry.Import].Size;
            return ReadAt(image, rvaToOffset, importRva, importSize);
        }

        public static IReadOnlyList<(string DllName, IReadOnlyList<(bool ByName, ushort Ordinal, string Name)>)> ReadAt(
            ReadOnlySpan<byte> image,
            Func<uint, uint> rvaToOffset,
            uint importRva,
            uint importSize)
        {
            var result = new List<(string, IReadOnlyList<(bool, ushort, string)>)>();
            if (importRva == 0 || importSize == 0)
            {
                return result;
            }

            var baseOffset = rvaToOffset(importRva);

            for (uint index = 0; ; index++)
            {
                var descriptorOffset = baseOffset + index * ImageImportDescriptor.SizeOfEntry;
                if (descriptorOffset + ImageImportDescriptor.SizeOfEntry > (uint)image.Length)
                {
                    break;
                }

                var descriptor = ImageImportDescriptor.Read(image.Slice((int)descriptorOffset, ImageImportDescriptor.SizeOfEntry));
                if (descriptor.IsEndOfArray)
                {
                    break;
                }

                var dllNameOffset = rvaToOffset(descriptor.Name);
                var dllName = ReadAscii(image, dllNameOffset);
                var thunkRva = descriptor.OriginalFirstThunk != 0 ? descriptor.OriginalFirstThunk : descriptor.FirstThunk;
                var entries = new List<(bool, ushort, string)>();
                for (uint i = 0; ; i++)
                {
                    var thunkOffset = rvaToOffset(thunkRva + i * (uint)ImageThunkData64.SizeOfEntry);
                    if (thunkOffset + ImageThunkData64.SizeOfEntry > (uint)image.Length)
                    {
                        break;
                    }

                    var thunk = ImageThunkData64.Read(image.Slice((int)thunkOffset, ImageThunkData64.SizeOfEntry));
                    if (thunk.IsNull)
                    {
                        break;
                    }

                    if (thunk.IsOrdinal)
                    {
                        entries.Add((false, thunk.OrdinalNumber, string.Empty));
                    }
                    else
                    {
                        var byName = ImageImportByName.Read(image.Slice((int)rvaToOffset(thunk.AddressOfDataRva)));
                        entries.Add((true, byName.Hint, byName.NameString));
                    }
                }

                result.Add((dllName, entries));
            }

            return result;
        }

        private static string ReadAscii(ReadOnlySpan<byte> image, uint offset)
        {
            var end = (int)offset;
            while (end < image.Length && image[end] != 0)
            {
                end++;
            }

            return Encoding.ASCII.GetString(image.Slice((int)offset, end - (int)offset));
        }
    }
}