using Cocoa.CodeAnalysis.Binding;

namespace Cocoa.CodeAnalysis.Symbols
{
    public class LocalVariableSymbol : VariableSymbol
    {
        public LocalVariableSymbol(string name, bool isReadOnly, TypeSymbol type, BoundConstant? constant)
            : base(name, isReadOnly, type, constant)
        {
        }

        public override SymbolKind Kind => SymbolKind.LocalVariable;
    }
}
