using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundScope
    {
        private Dictionary<string, Symbol> m_symbols;

        public BoundScope(BoundScope parent)
        {
            Parent = parent;
        }

        public BoundScope Parent { get; }

        public bool TryDeclareVariable(VariableSymbol variable) => TryDeclareSymbol(variable);

        public bool TryLookUpVariable(string name, out VariableSymbol variable) => TryLookupSymbol(name, out variable);

        public bool TryDeclareFunction(FunctionSymbol function) => TryDeclareSymbol(function);

        public bool TryLookUpFunction(string name, out FunctionSymbol function) => TryLookupSymbol(name, out function);

        public ImmutableArray<VariableSymbol> GetDeclaredVariables() => GetDeclaredSymbols<VariableSymbol>();

        public ImmutableArray<FunctionSymbol> GetDeclaredFunctions() => GetDeclaredSymbols<FunctionSymbol>();

        private bool TryDeclareSymbol<TSymbol>(TSymbol symbol) where TSymbol : Symbol
        {
            if (m_symbols == null)
            {
                m_symbols = new Dictionary<string, Symbol>();
            }
            else if (m_symbols.ContainsKey(symbol.Name))
            {
                return false;
            }

            m_symbols.Add(symbol.Name, symbol);

            return true;
        }

        private bool TryLookupSymbol<TSymbol>(string name, out TSymbol symbol) where TSymbol : Symbol
        {
            symbol = null;

            if (m_symbols != null && m_symbols.TryGetValue(name, out var declaredSymbol))
            {
                if (declaredSymbol is TSymbol matchingSymbol)
                {
                    symbol = matchingSymbol;
                    return true;
                }

                return false;
            }

            if (Parent == null)
            {
                return false;
            }

            return Parent.TryLookupSymbol(name, out symbol);
        }

        private ImmutableArray<TSymbol> GetDeclaredSymbols<TSymbol>() where TSymbol : Symbol
        {
            if (m_symbols == null)
            {
                return ImmutableArray<TSymbol>.Empty;
            }

            return m_symbols.Values.OfType<TSymbol>().ToImmutableArray();
        }
    }
}
