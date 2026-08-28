using System;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 单维数组类型符号（6e-M26 Phase 1-4：SymbolKind.Type 拆分）。
    /// 对齐 Roslyn <see cref="Microsoft.CodeAnalysis.ArrayTypeSymbol"/>：
    /// 数组从轻量 <c>TypeSymbol</c>（<see cref="SymbolKind.Type"/>）独立为专有子类，
    /// 使 <see cref="SymbolKind.Type"/> 仅剩 any/error/null/void/函数值等 CO 特殊类型。
    /// 实例经 <see cref="TypeSymbol.ArrayOf"/> 缓存创建；序列化仍走名字引用（name + []）。
    /// </summary>
    public sealed class ArrayTypeSymbol : TypeSymbol
    {
        internal ArrayTypeSymbol(TypeSymbol elementType)
            : base(elementType)
        {
        }

        public override SymbolKind Kind => SymbolKind.ArrayType;
    }
}