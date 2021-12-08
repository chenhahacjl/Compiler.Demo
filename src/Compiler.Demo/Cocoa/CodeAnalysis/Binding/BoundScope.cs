using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundScope
    {
        private Dictionary<string, VariableSymbol> m_variables = new Dictionary<string, VariableSymbol>();

        public BoundScope(BoundScope parent)
        {
            Parent = parent;
        }

        public BoundScope Parent { get; }

        public bool TryDeclare(VariableSymbol variable)
        {
            if (m_variables.ContainsKey(variable.Name))
            {
                return false;
            }

            m_variables.Add(variable.Name, variable);

            return true;
        }

        public bool TryLookUp(string name, out VariableSymbol variable)
        {
            if (m_variables.TryGetValue(name, out variable))
            {
                return true;
            }

            if (Parent == null)
            {
                return false;
            }

            return Parent.TryLookUp(name, out variable);
        }

        public ImmutableArray<VariableSymbol> GetDeclaredVariables()
        {
            return m_variables.Values.ToImmutableArray();
        }
    }
}
