using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    /// <summary>ImportTableBuilder 产物：blob + 各 DLL 布局。blob 不含 IAT 槽数据本体。</summary>
    public sealed class PeImportTableLayout
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
}
