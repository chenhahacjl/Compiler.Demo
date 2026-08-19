namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 类字段符号。
    /// </summary>
    public sealed class FieldSymbol : VariableSymbol
    {
        internal FieldSymbol(string name, TypeSymbol type, bool isPublic, ClassTypeSymbol containingClass)
            : base(name, isReadOnly: false, type, constant: null)
        {
            IsPublic = isPublic;
            ContainingClass = containingClass;
        }

        public override SymbolKind Kind => SymbolKind.Field;

        public bool IsPublic { get; }

        public ClassTypeSymbol ContainingClass { get; }
    }
}
