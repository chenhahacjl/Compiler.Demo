using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 类字段符号。
    /// </summary>
    public sealed class FieldSymbol : VariableSymbol
    {
        public FieldSymbol(string name, TypeSymbol type, Visibility visibility, NamedTypeSymbol containingClass, bool isReadonly = false, bool isStatic = false)
            : base(name, isReadOnly: isReadonly, type, constant: null)
        {
            Visibility = visibility;
            ContainingClass = containingClass;
            IsReadonly = isReadonly;
            IsStatic = isStatic;
        }

        public override SymbolKind Kind => SymbolKind.Field;

        /// <summary>声明上绑定的 attribute（6e-M32 Tier-2：字段级）。</summary>
        public ImmutableArray<AttributeSymbol> Attributes { get; set; } = ImmutableArray<AttributeSymbol>.Empty;

        public Visibility Visibility { get; }

        public NamedTypeSymbol ContainingClass { get; }

        /// <summary>readonly 字段（仅构造内可赋值）。</summary>
        public bool IsReadonly { get; }

        public bool IsStatic { get; set; }
    }
}
