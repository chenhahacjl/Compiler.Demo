using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 泛型类型参数符号（6e-M20）：泛型定义上下文内的"不透明类型"。
    /// 成员访问仅限约束开放（定义期绑定，G2）；实例化期被实参替换（单态化）。
    /// </summary>
    public sealed class TypeParameterSymbol : TypeSymbol
    {
        internal TypeParameterSymbol(string name, int ordinal, ClassTypeSymbol? owningClass)
            : base(name)
        {
            Ordinal = ordinal;
            OwningClass = owningClass;
        }

        public override SymbolKind Kind => SymbolKind.TypeParameter;

        /// <summary>类型参数序号（声明顺序，0 起）。</summary>
        public int Ordinal { get; }

        /// <summary>所属泛型类定义（类级类型参数；null = 顶层/独立上下文）。</summary>
        public ClassTypeSymbol? OwningClass { get; }

        /// <summary>约束类型列表（接口/基类；实例化期校验实参满足）。</summary>
        public ImmutableArray<TypeSymbol> ConstraintTypes { get; internal set; } = ImmutableArray<TypeSymbol>.Empty;

        /// <summary>`new()` 无参构造约束。</summary>
        public bool HasNewConstraint { get; internal set; }

        /// <summary>`class` 引用类型约束。</summary>
        public bool HasReferenceTypeConstraint { get; internal set; }

        /// <summary>`struct` 值类型约束（6e-M22 C1）：基元数值/bool/char + enum（语言暂无用户 struct）。</summary>
        public bool HasValueTypeConstraint { get; internal set; }
    }
}
