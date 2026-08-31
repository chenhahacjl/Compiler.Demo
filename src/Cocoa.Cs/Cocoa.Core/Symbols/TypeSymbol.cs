using System;
using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Symbols
{
    public class TypeSymbol : Symbol
    {
        public static readonly TypeSymbol Error = new TypeSymbol("?");
        public static readonly TypeSymbol Any = new TypeSymbol("any");

        /// <summary>6e-M19 M5-a：null 字面量类型——只存在于字面量瞬间，绑定期即转换到目标引用型（类/接口/string/数组/any）。</summary>
        public static readonly TypeSymbol Null = new TypeSymbol("null");
        public static readonly TypeSymbol Boolean = new NamedTypeSymbol("bool", "", Visibility.Public, null) { SpecialType = SpecialType.System_Boolean, TypeKind = TypeKind.Struct };
        public static readonly TypeSymbol Int8 = new NamedTypeSymbol("sbyte", "", Visibility.Public, null) { SpecialType = SpecialType.System_Int8, TypeKind = TypeKind.Struct };
        public static readonly TypeSymbol Int16 = new NamedTypeSymbol("short", "", Visibility.Public, null) { SpecialType = SpecialType.System_Int16, TypeKind = TypeKind.Struct };
        public static readonly TypeSymbol Int32 = new NamedTypeSymbol("int", "", Visibility.Public, null) { SpecialType = SpecialType.System_Int32, TypeKind = TypeKind.Struct };
        public static readonly TypeSymbol Int64 = new NamedTypeSymbol("long", "", Visibility.Public, null) { SpecialType = SpecialType.System_Int64, TypeKind = TypeKind.Struct };
        public static readonly TypeSymbol UInt16 = new NamedTypeSymbol("ushort", "", Visibility.Public, null) { SpecialType = SpecialType.System_UInt16, TypeKind = TypeKind.Struct };
        public static readonly TypeSymbol UInt32 = new NamedTypeSymbol("uint", "", Visibility.Public, null) { SpecialType = SpecialType.System_UInt32, TypeKind = TypeKind.Struct };
        public static readonly TypeSymbol UInt64 = new NamedTypeSymbol("ulong", "", Visibility.Public, null) { SpecialType = SpecialType.System_UInt64, TypeKind = TypeKind.Struct };
        public static readonly TypeSymbol UInt8 = new NamedTypeSymbol("byte", "", Visibility.Public, null) { SpecialType = SpecialType.System_UInt8, TypeKind = TypeKind.Struct };
        public static readonly TypeSymbol Double = new NamedTypeSymbol("double", "", Visibility.Public, null) { SpecialType = SpecialType.System_Double, TypeKind = TypeKind.Struct };
        public static readonly TypeSymbol Float = new NamedTypeSymbol("float", "", Visibility.Public, null) { SpecialType = SpecialType.System_Single, TypeKind = TypeKind.Struct };
        public static readonly TypeSymbol Char = new NamedTypeSymbol("char", "", Visibility.Public, null) { SpecialType = SpecialType.System_Char, TypeKind = TypeKind.Struct };
        public static readonly TypeSymbol String = new NamedTypeSymbol("string", "", Visibility.Public, null) { SpecialType = SpecialType.System_String, TypeKind = TypeKind.Class };
        public static readonly TypeSymbol Void = new TypeSymbol("void");
        public static readonly TypeSymbol Int128 = new TypeSymbol("i128");
        public static readonly TypeSymbol UInt128 = new TypeSymbol("u128");
        public static readonly TypeSymbol Float128 = new TypeSymbol("f128");

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<TypeSymbol, TypeSymbol> _arrayTypes = new System.Collections.Concurrent.ConcurrentDictionary<TypeSymbol, TypeSymbol>();

        internal TypeSymbol(string name)
            : base(name)
        {

        }

        internal TypeSymbol(TypeSymbol elementType)
            : base(elementType.Name + "[]")
        {
            ElementType = elementType;
        }

        /// <summary>null 表示基元类型；非 null 表示该类型的数组。</summary>
        public TypeSymbol? ElementType { get; }

        public static TypeSymbol ArrayOf(TypeSymbol elementType)
        {
            return _arrayTypes.GetOrAdd(elementType, static e => new ArrayTypeSymbol(e));
        }

        public override SymbolKind Kind => SymbolKind.Type;

        /// <summary>知名 BCL 类型标记（默认 None；基元单例构造时赋值，对齐 Roslyn SpecialType）。</summary>
        public SpecialType SpecialType { get; internal set; } = SpecialType.None;

        /// <summary>基元值类型（bool/数值/char，含 8/16/32/64/128 位；不含 string/void/error/any/null）。
        /// 引用相等判定——无论实例是轻量 TypeSymbol 还是 NamedTypeSymbol（C3），单例不变故恒成立。</summary>
        public bool IsPrimitiveValueType =>
            this == Boolean || this == Int8 || this == Int16 || this == Int32 || this == Int64 ||
            this == UInt8 || this == UInt16 || this == UInt32 || this == UInt64 ||
            this == Int128 || this == UInt128 || this == Float || this == Double || this == Char;

        /// <summary>是否为值类型（基元值类型 + 用户 struct/enum；NamedTypeSymbol 覆盖见下）。</summary>
        public virtual bool IsValueType => IsPrimitiveValueType;

        /// <summary>是否为引用类型（类/接口/delegate/string/any/数组/函数值）。</summary>
        public bool IsReferenceType => !IsValueType;

        /// <summary>是否为整数类型（含有/无符号 8/16/32/64/128 位，不含 bool/char）。</summary>
        public bool IsInteger =>
            this == Int8 || this == Int16 || this == Int32 || this == Int64 ||
            this == UInt16 || this == UInt32 || this == UInt64 || this == UInt8 ||
            this == Int128 || this == UInt128;

        /// <summary>是否为有符号整数类型。</summary>
        public bool IsSigned =>
            this == Int8 || this == Int16 || this == Int32 || this == Int64 || this == Int128;

        /// <summary>是否为浮点类型（32/64/128 位）。</summary>
        public bool IsFloat => this == Float || this == Double || this == Float128;

        /// <summary>是否为数值类型（整数或浮点）。</summary>
        public bool IsNumeric => IsInteger || IsFloat;

        /// <summary>整数位宽；非整数返回 0。</summary>
        public int BitWidth =>
            this == Int8 || this == UInt8 ? 8 :
            this == Int16 || this == UInt16 ? 16 :
            this == Int32 || this == UInt32 || this == Float ? 32 :
            this == Int64 || this == UInt64 || this == Double ? 64 :
            this == Int128 || this == UInt128 || this == Float128 ? 128 : 0;

        /// <summary>128 位类型（暂未实装，仅占位识别）。</summary>
        public bool IsPlaceholder128 => this == Int128 || this == UInt128 || this == Float128;
    }
}