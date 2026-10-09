namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 内建类型名解析（去 <c>Language</c> 门面后独立；Cocoa 简写词汇 + 共享基元名）。
    /// 内建类型简写 i8/u8/i16/u16/i32/u32/i64/u64/f32/f64（+ i128/u128/f128 占位）。
    /// </summary>
    public static class BuiltinTypes
    {
        /// <summary>内建类型名（any/bool/char/string/void 共享 + Cocoa 简写词汇）；未命中返回 null。</summary>
        public static TypeSymbol? Lookup(string name) => name switch
        {
            "any" => TypeSymbol.Any,
            "bool" => TypeSymbol.Boolean,
            "char" => TypeSymbol.Char,
            "string" => TypeSymbol.String,
            "void" => TypeSymbol.Void,
            "i8" => TypeSymbol.Int8,
            "u8" => TypeSymbol.UInt8,
            "i16" => TypeSymbol.Int16,
            "u16" => TypeSymbol.UInt16,
            "i32" => TypeSymbol.Int32,
            "u32" => TypeSymbol.UInt32,
            "i64" => TypeSymbol.Int64,
            "u64" => TypeSymbol.UInt64,
            "f32" => TypeSymbol.Float,
            "f64" => TypeSymbol.Double,
            "decimal" => TypeSymbol.Decimal,
            "half" => TypeSymbol.Half,
            "i128" => TypeSymbol.Int128,
            "u128" => TypeSymbol.UInt128,
            "f128" => TypeSymbol.Float128,
            "nint" => TypeSymbol.NativeInt32,
            "nuint" => TypeSymbol.NativeUInt32,
            _ => null,
        };
    }
}
