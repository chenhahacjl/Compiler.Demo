using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundScope
    {
        private Dictionary<string, VariableSymbol> m_variables;
        private Dictionary<string, FunctionSymbol> m_functions;

        public BoundScope(BoundScope parent)
        {
            Parent = parent;
        }

        public BoundScope Parent { get; }

        public bool TryDeclareVariable(VariableSymbol variable)
        {
            if (m_variables == null)
            {
                m_variables = new Dictionary<string, VariableSymbol>();
            }

            if (m_variables.ContainsKey(variable.Name))
            {
                return false;
            }

            m_variables.Add(variable.Name, variable);

            return true;
        }

        public bool TryLookUpVariable(string name, out VariableSymbol variable)
        {
            variable = null;

            if (m_variables != null && m_variables.TryGetValue(name, out variable))
            {
                return true;
            }

            if (Parent == null)
            {
                return false;
            }

            return Parent.TryLookUpVariable(name, out variable);
        }

        public bool TryDeclareFunction(FunctionSymbol function)
        {
            if (m_functions == null)
            {
                m_functions = new Dictionary<string, FunctionSymbol>();
            }

            if (m_functions.ContainsKey(function.Name))
            {
                return false;
            }

            m_functions.Add(function.Name, function);

            return true;
        }

        public bool TryLookUpFunction(string name, out FunctionSymbol function)
        {
            function = null;

            if (m_functions != null && m_functions.TryGetValue(name, out function))
            {
                return true;
            }

            if (Parent == null)
            {
                return false;
            }

            return Parent.TryLookUpFunction(name, out function);
        }

        public ImmutableArray<VariableSymbol> GetDeclaredVariables()
        {
            if (m_variables == null)
            {
                return ImmutableArray<VariableSymbol>.Empty;
            }

            return m_variables.Values.ToImmutableArray();
        }

        public ImmutableArray<FunctionSymbol> GetDeclaredFunctions()
        {
            if (m_functions == null)
            {
                return ImmutableArray<FunctionSymbol>.Empty;
            }

            return m_functions.Values.ToImmutableArray();
        }
    }
}
