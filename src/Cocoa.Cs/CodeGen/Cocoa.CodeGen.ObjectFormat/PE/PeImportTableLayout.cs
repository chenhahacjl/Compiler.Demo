using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.ObjectFormat.PE
{
    /// <summary>ImportTableBuilder 产物：blob + 各 DLL 布局。blob 含描述符/INT/hint-name；内部模式下含 IAT 数组。</summary>
    public sealed class PeImportTableLayout
    {
        public PeImportTableLayout(byte[] blob, int descriptorsOffset, IReadOnlyList<PeImportDllLayout> dlls, IReadOnlyList<int>? iatSlotOffsets = null)
        {
            Blob = blob;
            DescriptorsOffset = descriptorsOffset;
            Dlls = dlls;
            IatSlotOffsets = iatSlotOffsets ?? Array.Empty<int>();
        }

        public byte[] Blob { get; }
        public int DescriptorsOffset { get; }
        public IReadOnlyList<PeImportDllLayout> Dlls { get; }

        /// <summary>按输入 specs 顺序的 blob 内 IAT 槽偏移（内部 IAT 模式填充；外部模式为空）。</summary>
        public IReadOnlyList<int> IatSlotOffsets { get; }
    }
}
