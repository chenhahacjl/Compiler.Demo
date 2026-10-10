using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    /// <summary>每个 DLL 的导入区块布局（offset 相对 blob 起始）。</summary>
    public sealed class PeImportDllLayout
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
}
