namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 类属性符号（getter/setter 方法）。
    /// </summary>
    public sealed class PropertySymbol : Symbol
    {
        internal PropertySymbol(string name, TypeSymbol type, ClassTypeSymbol containingClass, FunctionSymbol? getter, FunctionSymbol? setter, Visibility visibility, bool isStatic)
            : base(name)
        {
            Type = type;
            ContainingClass = containingClass;
            Getter = getter;
            Setter = setter;
            Visibility = visibility;
            IsStatic = isStatic;
        }

        public override SymbolKind Kind => SymbolKind.Property;

        public TypeSymbol Type { get; }
        public ClassTypeSymbol ContainingClass { get; }
        public FunctionSymbol? Getter { get; }
        public FunctionSymbol? Setter { get; }
        public Visibility Visibility { get; }
        public bool IsStatic { get; }
    }
}
