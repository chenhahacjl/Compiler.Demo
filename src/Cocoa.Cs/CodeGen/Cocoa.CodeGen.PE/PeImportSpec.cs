using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    /// <summary>导入规格：DLL 名 + 函数名（或 ordinal）。Ordinal==0 表示按名字导入。</summary>
    public readonly record struct PeImportSpec(string DllName, string FunctionName, ushort Ordinal = 0)
    {
        public bool ByName => Ordinal == 0;
    }
}
