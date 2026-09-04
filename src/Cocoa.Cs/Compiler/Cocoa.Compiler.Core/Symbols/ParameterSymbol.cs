namespace Cocoa.CodeAnalysis.Symbols
{
    public sealed class ParameterSymbol : LocalVariableSymbol
    {
        public ParameterSymbol(string name, TypeSymbol type, int ordinal, bool isOut = false, bool isRef = false, bool isThis = false)
            : base(name, isReadOnly: isThis, type, null)
        {
            Ordinal = ordinal;
            IsOut = isOut;
            IsRef = isRef;
        }

        public override SymbolKind Kind => SymbolKind.Parameter;

        public int Ordinal { get; }

        /// <summary>out 形参（6e-M23）：入口未赋值（DFA 强制出口前赋值），被调方写入调用方实参。</summary>
        public bool IsOut { get; }

        /// <summary>ref 形参（6e-M23）：双向别名，调用方须已赋值。</summary>
        public bool IsRef { get; }

        public bool IsByRef => IsOut || IsRef;

        public bool IsThisParameter => IsReadOnly;
    }
}
