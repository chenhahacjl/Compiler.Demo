using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Bound;

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
