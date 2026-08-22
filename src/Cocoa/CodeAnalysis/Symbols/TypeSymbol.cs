using System;
using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Symbols
{
    public class TypeSymbol : Symbol
    {
        public static readonly TypeSymbol Error = new TypeSymbol("?");
        public static readonly TypeSymbol Any = new TypeSymbol("any");
        public static readonly TypeSymbol Boolean = new TypeSymbol("bool");
        public static readonly TypeSymbol Int32 = new TypeSymbol("int");
        public static readonly TypeSymbol Long = new TypeSymbol("long");
        public static readonly TypeSymbol Byte = new TypeSymbol("byte");
        public static readonly TypeSymbol Double = new TypeSymbol("double");
        public static readonly TypeSymbol Char = new TypeSymbol("char");
        public static readonly TypeSymbol String = new TypeSymbol("string");
        public static readonly TypeSymbol Void = new TypeSymbol("void");

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
    }
}