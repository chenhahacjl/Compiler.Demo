namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 知名 BCL 类型标记（对齐 Roslyn <see cref="Microsoft.CodeAnalysis.SpecialType"/>）：
    /// 用于 cheap 识别 int/string/object/void 等内建类型，避免散落的引用相等比对。
    /// CO 特有类型（any/error/null）与用户类型、外部非知名类型均为 <see cref="None"/>。
    /// </summary>
    public enum SpecialType
    {
        None,
        System_Object,
        System_String,
        System_Char,
        System_Boolean,
        System_Int8,    // sbyte
        System_UInt8,   // byte
        System_Int16,   // short
        System_UInt16,  // ushort
        System_Int32,   // int
        System_UInt32,  // uint
        System_Int64,   // long
        System_UInt64,  // ulong
        System_Single,  // float
        System_Double,  // double
        System_Int128,
        System_UInt128,
        System_Void,
    }
}
