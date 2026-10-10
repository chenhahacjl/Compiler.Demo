using System;
using System.Buffers.Binary;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    /// <summary>导出目录索引（供 stub 生成的机器码与 C# 参照共用同一布局知识）。</summary>
    public readonly record struct PeExportIndex(
        uint ExportDirRva,
        uint OrdinalBase,
        uint FunctionCount,
        uint NameCount,
        uint FunctionsRva,
        uint NamesRva,
        uint NameOrdinalsRva)
    {
        public static PeExportIndex FromDirectory(ImageExportDirectory dir)
        {
            return new PeExportIndex(0, dir.Base, dir.NumberOfFunctions, dir.NumberOfNames, dir.AddressOfFunctions, dir.AddressOfNames, dir.AddressOfNameOrdinals);
        }
    }
}
