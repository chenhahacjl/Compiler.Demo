using Cocoa.CodeAnalysis.Binding;

namespace Cocoa.CodeAnalysis.Symbols
{
    public sealed class GlobalVariableSymbol : VariableSymbol
    {
        public GlobalVariableSymbol(string name, bool isReadOnly, TypeSymbol type, BoundConstant? constant)
            : base(name, isReadOnly, type, constant)
        {
        }

        public override SymbolKind Kind => SymbolKind.GlobalVariable;
    }
}
