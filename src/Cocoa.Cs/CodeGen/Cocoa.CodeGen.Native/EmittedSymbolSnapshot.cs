using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeGen.Native.Lir;

namespace Cocoa.CodeGen.Native
{
    /// <summary>
    /// native 发射后实际符号集快照：存活类（new 可达）+ 实际发射函数映射。
    /// 纯暴露、无逻辑拷贝——从 <see cref="MirToLir"/> 内部字段捕获后转为只读集合。
    /// </summary>
    public sealed class EmittedSymbolSnapshot
    {
        internal EmittedSymbolSnapshot(ImmutableArray<NamedTypeSymbol> liveClasses, ImmutableDictionary<FunctionSymbol, LirFunction> functions)
        {
            LiveClasses = liveClasses;
            Functions = functions;
        }

        /// <summary>存活类集合（new 可达 → 类 + 基类链），按 FullName 稳定排序。</summary>
        public ImmutableArray<NamedTypeSymbol> LiveClasses { get; }

        /// <summary>实际发射函数 → LIR 函数（含 lambda/单态化泛型展开后的实例）。</summary>
        public ImmutableDictionary<FunctionSymbol, LirFunction> Functions { get; }
    }
}
