using System;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 程序集符号（Phase 1-5 起点，对齐 Roslyn <see cref="Microsoft.CodeAnalysis.IAssemblySymbol"/>）。
    /// 源程序集 = 本编译的输出程序集；元数据程序集 = 引用的 `.cod` 库。
    /// 现有 <c>string[] references</c> / <c>CodProgram</c> 模型不变（本类为新增视图，非替换）。
    /// </summary>
    public sealed class AssemblySymbol : Symbol
    {
        internal AssemblySymbol(string name, bool isSource)
            : base(name)
        {
            IsSourceAssembly = isSource;
        }

        public override SymbolKind Kind => SymbolKind.Assembly;

        /// <summary>是否本编译的源程序集（false = 引用的元数据程序集）。</summary>
        public bool IsSourceAssembly { get; }
    }
}