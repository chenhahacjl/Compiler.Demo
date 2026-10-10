using System;
using System.Collections.Generic;
using System.IO;
using Cocoa.Targeting;
using System.Linq;
using static Cocoa.CodeGen.PE.PeBinary;

namespace Cocoa.CodeGen.PE
{
    /// <summary>导入规格：DLL 名 + 函数名 + IAT 槽在 .idata blob 内的偏移。</summary>
    public readonly record struct PefileImport(string DllName, string Name, int IatOffset);
}
