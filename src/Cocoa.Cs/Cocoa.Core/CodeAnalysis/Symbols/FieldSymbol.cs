namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 类字段符号。
    /// </summary>
    public sealed class FieldSymbol : VariableSymbol
    {
        internal FieldSymbol(string name, TypeSymbol type, Visibility visibility, ClassTypeSymbol containingClass, bool isReadonly = false, bool isStatic = false)
            : base(name, isReadOnly: isReadonly, type, constant: null)
        {
            Visibility = visibility;
            ContainingClass = containingClass;
            IsReadonly = isReadonly;
            IsStatic = isStatic;
        }

        public override SymbolKind Kind => SymbolKind.Field;

        public Visibility Visibility { get; }

        public ClassTypeSymbol ContainingClass { get; }

        /// <summary>readonly 字段（仅构造内可赋值）。</summary>
        public bool IsReadonly { get; }

        public bool IsStatic { get; internal set; }
    }
}
