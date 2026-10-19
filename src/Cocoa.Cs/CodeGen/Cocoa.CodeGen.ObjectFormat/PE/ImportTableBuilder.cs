using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.ObjectFormat.PE
{
    /// <summary>按 DLL 分组的导入表构建器：DLL 名 / INT / HintName / IAT / descriptor 组 + 全零终止。
    /// 自包含形态：descriptors + INT + hint/name + 内嵌 IAT 同处一个 blob（.idata 节），dir1/dir12/FirstThunk 均指向 .idata。</summary>
    public static class ImportTableBuilder
    {
        public static PeImportTableLayout Build(
            IReadOnlyList<PeImportSpec> specs,
            uint blobBaseRva = 0,
            bool pe32 = false,
            IReadOnlyList<int>? externalIatOffsets = null,
            uint externalIatRva = 0,
            bool zeroIatArray = false)
        {
            var dllNames = new List<string>();
            var byDll = new List<List<PeImportSpec>>();
            var firstIndexByDll = new List<int>();

            for (var s = 0; s < specs.Count; s++)
            {
                var spec = specs[s];
                var index = dllNames.IndexOf(spec.DllName);
                if (index < 0)
                {
                    dllNames.Add(spec.DllName);
                    byDll.Add(new List<PeImportSpec>());
                    firstIndexByDll.Add(s);
                    index = byDll.Count - 1;
                }

                byDll[index].Add(spec);
            }

            var parts = new List<byte>();
            var pending = new List<(string Dll, int DllNameOffset, int IntOffset, int IatOffset, List<(PeImportSpec Spec, int HintNameOffset)> Entries)>();

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

                // 外部 IAT 模式：FirstThunk 指向 data 区槽数组（加载器填充），blob 内不生成副本
                var iatOffset = externalIatOffsets != null
                    ? externalIatOffsets[firstIndexByDll[dllNames.IndexOf(dll)]]
                    : parts.Count;

                if (externalIatOffsets == null)
                {
                    foreach (var entry in entries)
                    {
                        // IAT 内嵌 blob：默认镜像 INT（hint/name RVA，Windows 加载器"verify-then-replace"依赖非零槽）；
                        // zeroIatArray 时写 0（csc 标准形态，供实验/择优）。
                        if (zeroIatArray)
                        {
                            WriteThunk(parts, null, blobBaseRva, pe32);
                        }
                        else
                        {
                            WriteThunk(parts, entry, blobBaseRva, pe32);
                        }
                    }

                    WriteThunk(parts, null, blobBaseRva, pe32);
                }

                pending.Add((dll, dllNameOffset, intOffset, iatOffset, entries));
            }

            // 描述符数组必须连续排列：Windows 加载器从导入目录 RVA 起按 20 字节步进遍历，
            // 每个 DLL 一个描述符，随后是全零终止项（interleaved 布局会让多 DLL 时第二个模块
            // 的数据被当成垃圾描述符，加载器读取越界内存）。
            var descriptorsOffset = parts.Count;
            var layouts = new List<PeImportDllLayout>(pending.Count);
            for (var i = 0; i < pending.Count; i++)
            {
                var p = pending[i];
                var descriptorOffset = descriptorsOffset + i * ImageImportDescriptor.SizeOfEntry;
                WriteUInt32(parts, 0);
                WriteUInt32(parts, 0);
                WriteUInt32(parts, 0);
                WriteUInt32(parts, (int)(blobBaseRva + (uint)p.DllNameOffset));
                WriteUInt32(parts, 0);
                layouts.Add(new PeImportDllLayout(p.Dll, descriptorOffset, p.IntOffset, p.IatOffset, p.DllNameOffset, p.Entries));
            }

            parts.AddRange(new byte[ImageImportDescriptor.SizeOfEntry]);

            for (var i = 0; i < layouts.Count; i++)
            {
                var layout = layouts[i];
                WriteUInt32(parts, layout.DescriptorOffset, (int)(blobBaseRva + (uint)layout.IntOffset));
                WriteUInt32(parts, layout.DescriptorOffset + 16, externalIatOffsets != null
                    ? (int)(externalIatRva + (uint)layout.IatOffset)
                    : (int)(blobBaseRva + (uint)layout.IatOffset));
            }

            // 内部 IAT 布局下，导出每个函数的 blob 内 IAT 槽偏移（按输入 specs 顺序；供代码寻址与目录 index 12）。
            var iatSlotOffsets = new int[specs.Count];
            if (externalIatOffsets == null)
            {
                var thunkSize = pe32 ? 4 : 8;
                for (var g = 0; g < pending.Count; g++)
                {
                    var p = pending[g];
                    for (var e = 0; e < p.Entries.Count; e++)
                    {
                        iatSlotOffsets[firstIndexByDll[g] + e] = p.IatOffset + e * thunkSize;
                    }
                }
            }

            return new PeImportTableLayout(parts.ToArray(), descriptorsOffset, layouts, iatSlotOffsets);
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
}
