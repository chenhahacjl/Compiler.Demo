namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 命名类型类别（6e-M26，对齐 Roslyn <see cref="Microsoft.CodeAnalysis.TypeKind"/>）：
    /// class/struct/interface/enum/delegate 共用同一 <see cref="NamedTypeSymbol"/>，以本枚举判别。
    /// </summary>
    public enum TypeKind
    {
        /// <summary>类（引用类型）。</summary>
        Class,

        /// <summary>结构体（值类型）。</summary>
        Struct,

        /// <summary>接口。</summary>
        Interface,

        /// <summary>枚举（值类型，底层 int）。</summary>
        Enum,

        /// <summary>委托。</summary>
        Delegate,
    }
}
