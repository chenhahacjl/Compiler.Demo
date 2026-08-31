using System;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 程序集符号（Phase 1-5 起点，对齐 Roslyn <see cref="Microsoft.CodeAnalysis.IAssemblySymbol"/>）。
    /// 源程序集 = 本编译的输出程序集；元数据程序集 = 引用的 `.cod` 库 / 程序集路径。
    /// </summary>
    public sealed class AssemblySymbol : Symbol
    {
        internal AssemblySymbol(string name, bool isSource, string? display = null)
            : base(name)
        {
            IsSourceAssembly = isSource;
            Display = display;
        }

        public override SymbolKind Kind => SymbolKind.Assembly;

        /// <summary>是否本编译的源程序集（false = 引用的元数据程序集）。</summary>
        public bool IsSourceAssembly { get; }

        /// <summary>元数据程序集的路径（供 Emit 解析 BCL/引用）；源程序集为 null。</summary>
        public string? Display { get; }
    }
}