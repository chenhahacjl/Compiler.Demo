using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Cocoa.CodeAnalysis.Emit.Native.PEFile
{
    /// <summary>瀵煎叆瑙勬牸锛欴LL 鍚?+ 鍑芥暟鍚?+ IAT 妲藉湪 .idata blob 鍐呯殑鍋忕Щ銆?/summary>
    internal readonly record struct PefileImport(string DllName, string Name, int IatOffset);

    /// <summary>PE 鍐欏嚭鍣細缁勮澶寸粨鏋勫眰 + 瀵煎叆琛ㄧ粨鏋勫眰锛屼骇鍑?PE32+ / PE32 闀滃儚鏂囦欢銆?/summary>
    internal static class PeFileWriter
    {
        public const int TextRva = 0x1000;
        public const int DataRva = 0x2000;
        public const int IdataRva = 0x5000;
        public const int SizeOfHeaders = 0x1000;

        private const int SectionAlignment = 0x1000;

        /// <summary>鏃ц矾寰勶紙RuntimeEmitter 鎵嬪伐甯冨眬锛変娇鐢ㄧ殑鍥哄畾鏁版嵁娈?RVA銆傛柊璺緞椤荤敤 ComputeDataRva 鍔ㄦ€佸竷灞€銆?/summary>
        public static int ComputeDataRva(int codeLength)
        {
            return Align(TextRva + codeLength, SectionAlignment);
        }

        public static int ComputeIdataRva(int codeLength, int dataLength)
        {
            return Align(ComputeDataRva(codeLength) + dataLength, SectionAlignment);
        }

        private static int Align(int value, int alignment)
        {
            var remainder = value % alignment;
            return remainder == 0 ? value : value + alignment - remainder;
        }

        public static long ImageBaseOf(Architecture architecture)
        {
            return architecture == Architecture.X86 ? 0x400000 : 0x140000000;
        }

        public static void Write(string outputPath, byte[] code, byte[] data, int entryPointRva, IReadOnlyList<PefileImport> imports, Architecture architecture)
        {
            var pe32 = architecture == Architecture.X86;

            var dataRva = ComputeDataRva(code.Length);
            var idataRva = ComputeIdataRva(code.Length, data.Length);

            var specs = new List<PeImportSpec>();
            foreach (var import in imports)
            {
                specs.Add(new PeImportSpec(import.DllName, import.Name));
            }

            var slotOffsets = imports.Select(i => i.IatOffset).ToList();
            var importLayout = ImportTableBuilder.Build(specs, (uint)idataRva, pe32, slotOffsets, (uint)dataRva);

            // 6c-2锛欼AT 妲界鐩樺垵鍊?= hintname RVA锛坢ingw fake-IAT 鎯緥锛夈€俉indows 鍔犺浇鍣ㄥ
            // 鍒濆€间负 0 鐨勬Ы瑙嗕负宸插～鍏呰€岃烦杩囷紙bound-import 璇箟锛夛紝鍙湁鎸囧悜 INT 鍖哄煙鐨勨€滀吉鍊尖€?
            // 鎵嶄細琚浛鎹负瑙ｆ瀽鍚庣殑鐪熷疄鍑芥暟鍦板潃锛屾晠妲戒笉鑳界暀闆躲€?
            var slotData = new byte[data.Length];
            Array.Copy(data, slotData, data.Length);
            foreach (var dll in importLayout.Dlls)
            {
                for (var e = 0; e < dll.Entries.Count; e++)
                {
                    var entry = dll.Entries[e];
                    var import = imports.First(i =>
                        string.Equals(i.DllName, entry.Spec.DllName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(i.Name, entry.Spec.FunctionName, StringComparison.OrdinalIgnoreCase));
                    var hintNameRva = (uint)idataRva + (uint)entry.HintNameOffset;
                    if (pe32)
                    {
                        WriteUInt32(slotData, import.IatOffset, (int)hintNameRva);
                    }
                    else
                    {
                        WriteUInt64(slotData, import.IatOffset, hintNameRva);
                    }
                }
            }

            var directories = new List<(PeDataDirectoryEntry Entry, uint Rva, uint Size)>();
            if (imports.Count > 0)
            {
                var iatEntrySize = pe32 ? ImageThunkData32.SizeOfEntry : ImageThunkData64.SizeOfEntry;
                var iatSlotCount = importLayout.Dlls.Sum(d => d.Entries.Count);
                var firstIatRva = (uint)dataRva + (uint)importLayout.Dlls[0].IatOffset;
                directories.Add((PeDataDirectoryEntry.Import, (uint)idataRva + (uint)importLayout.DescriptorsOffset, (uint)((importLayout.Dlls.Count + 1) * ImageImportDescriptor.SizeOfEntry)));
                directories.Add((PeDataDirectoryEntry.Iat, firstIatRva, (uint)(iatSlotCount * iatEntrySize)));
            }

            // 鍙墽琛岃妭铏氭嫙澶у皬涓嶅緱瓒呰繃 SectionAlignment锛?x1000锛夛紝鏁呬唬鐮佹寜椤垫媶鍒嗕负澶氫釜 .text 鑺傦紱
            // 鍚勮妭铏氭嫙鏈锛堝榻愬悗锛夊繀椤绘伆濂借惤鍦ㄤ笅涓€鑺傝捣鐐癸紙Windows 鍔犺浇鍣ㄦ寜鐩搁偦鑺傝繛缁牎楠岋級銆?
            var sections = new List<PeSectionSpec>();
            var codeOffset = 0;
            var codeSectionIndex = 0;
            while (codeOffset < code.Length)
            {
                var chunkLength = Math.Min(SectionAlignment, code.Length - codeOffset);
                var chunk = new byte[chunkLength];
                Array.Copy(code, codeOffset, chunk, 0, chunkLength);
                var name = codeSectionIndex == 0 ? ".text" : ".text" + codeSectionIndex.ToString();
                sections.Add(new PeSectionSpec(name, chunk, (uint)(TextRva + codeOffset), PeSectionCharacteristics.Text));
                codeOffset += chunkLength;
                codeSectionIndex++;
            }

            sections.Add(new(".data", slotData, (uint)dataRva, PeSectionCharacteristics.Data));
            sections.Add(new(".idata", importLayout.Blob, (uint)idataRva, PeSectionCharacteristics.Data));

            var config = new PeImageConfig(
                pe32 ? PeMachine.I386 : PeMachine.AMD64,
                (ulong)ImageBaseOf(architecture),
                (ushort)PeSubsystem.WindowsCui,
                pe32 ? (ushort)(PeDllCharacteristics.NxChipCompat | PeDllCharacteristics.NoSeh | PeDllCharacteristics.TerminalServerAware)
                     : (ushort)(PeDllCharacteristics.CurrentImage | PeDllCharacteristics.TerminalServerAware),
                (uint)entryPointRva)
            {
                SizeOfHeaders = (uint)SizeOfHeaders,
            };

            var image = PeImageBuilder.Build(config, sections, directories, pe32 ? PeFileCharacteristics.RelocsStripped : (ushort)0);
            File.WriteAllBytes(outputPath, image);
        }

        private static void WriteUInt32(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteUInt64(byte[] bytes, int offset, long value)
        {
            for (var i = 0; i < 8; i++)
            {
                bytes[offset + i] = (byte)(value >> (i * 8));
            }
        }
    }
}