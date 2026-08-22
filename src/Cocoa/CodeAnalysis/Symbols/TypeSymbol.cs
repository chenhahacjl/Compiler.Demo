using System;
using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Symbols
{
    public class TypeSymbol : Symbol
    {
        public static readonly TypeSymbol Error = new TypeSymbol("?");
        public static readonly TypeSymbol Any = new TypeSymbol("any");
        public static readonly TypeSymbol Boolean = new TypeSymbol("bool");
        public static readonly TypeSymbol Int8 = new TypeSymbol("sbyte");
        public static readonly TypeSymbol Int16 = new TypeSymbol("short");
        public static readonly TypeSymbol Int32 = new TypeSymbol("int");
        public static readonly TypeSymbol Int64 = new TypeSymbol("long");
        public static readonly TypeSymbol UInt16 = new TypeSymbol("ushort");
        public static readonly TypeSymbol UInt32 = new TypeSymbol("uint");
        public static readonly TypeSymbol UInt64 = new TypeSymbol("ulong");
        public static readonly TypeSymbol UInt8 = new TypeSymbol("byte");
        public static readonly TypeSymbol Double = new TypeSymbol("double");
        public static readonly TypeSymbol Float32 = new TypeSymbol("float");
        public static readonly TypeSymbol Char = new TypeSymbol("char");
        public static readonly TypeSymbol String = new TypeSymbol("string");
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
            return _arrayTypes.GetOrAdd(elementType, static e => new TypeSymbol(e));
        }

        public override SymbolKind Kind => SymbolKind.Type;

        /// <summary>是否为整数类型（含有/无符号 8/16/32/64/128 位，不含 bool/char）。</summary>
        public bool IsInteger =>
            this == Int8 || this == Int16 || this == Int32 || this == Int64 ||
            this == UInt16 || this == UInt32 || this == UInt64 || this == UInt8 ||
            this == Int128 || this == UInt128;

        /// <summary>是否为有符号整数类型。</summary>
        public bool IsSigned =>
            this == Int8 || this == Int16 || this == Int32 || this == Int64 || this == Int128;

        /// <summary>是否为浮点类型（32/64/128 位）。</summary>
        public bool IsFloat => this == Float32 || this == Double || this == Float128;

        /// <summary>是否为数值类型（整数或浮点）。</summary>
        public bool IsNumeric => IsInteger || IsFloat;

        /// <summary>整数位宽；非整数返回 0。</summary>
        public int BitWidth =>
            this == Int8 || this == UInt8 ? 8 :
            this == Int16 || this == UInt16 ? 16 :
            this == Int32 || this == UInt32 || this == Float32 ? 32 :
            this == Int64 || this == UInt64 || this == Double ? 64 :
            this == Int128 || this == UInt128 || this == Float128 ? 128 : 0;

        /// <summary>128 位类型（暂未实装，仅占位识别）。</summary>
        public bool IsPlaceholder128 => this == Int128 || this == UInt128 || this == Float128;
    }
}