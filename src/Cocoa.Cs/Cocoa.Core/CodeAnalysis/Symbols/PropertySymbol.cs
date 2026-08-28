namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 类属性符号（getter/setter 方法）。
    /// </summary>
    public sealed class PropertySymbol : Symbol
    {
        internal PropertySymbol(string name, TypeSymbol type, NamedTypeSymbol containingClass, FunctionSymbol? getter, FunctionSymbol? setter, Visibility visibility, bool isStatic, bool isIndexer = false)
            : base(name)
        {
            Type = type;
            ContainingClass = containingClass;
            Getter = getter;
            Setter = setter;
            Visibility = visibility;
            IsStatic = isStatic;
            IsIndexer = isIndexer;
        }

        public override SymbolKind Kind => SymbolKind.Property;

        public TypeSymbol Type { get; }
        public NamedTypeSymbol ContainingClass { get; }
        public FunctionSymbol? Getter { get; }
        public FunctionSymbol? Setter { get; }
        public Visibility Visibility { get; }
        public bool IsStatic { get; }

        /// <summary>索引器（this[...]）：重定向到 BCL get_Item/set_Item；成员访问经普通调用发射。6e-M24。</summary>
        public bool IsIndexer { get; }
    }
}
