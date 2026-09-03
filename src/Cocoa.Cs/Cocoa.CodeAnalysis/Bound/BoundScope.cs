using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;
using System.Linq;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundScope
    {
        private Dictionary<string, Symbol>? _symbols;
        private Dictionary<string, List<FunctionSymbol>>? _functions;
        private Dictionary<string, List<FunctionSymbol>>? _namespaceFunctions;

        public BoundScope(BoundScope? parent)
        {
            Parent = parent;
        }

        public BoundScope? Parent { get; }

        public bool TryDeclareVariable(VariableSymbol variable) => TryDeclareNonFunctionSymbol(variable);

        public bool TryDeclareEnum(NamedTypeSymbol enumType) => TryDeclareNonFunctionSymbol(enumType);

        public bool TryDeclareClass(NamedTypeSymbol classType) => TryDeclareNonFunctionSymbol(classType);

        private bool TryDeclareNonFunctionSymbol(Symbol symbol)
        {
            if (_symbols != null && _symbols.ContainsKey(symbol.Name))
            {
                return false;
            }

            if (_functions != null && _functions.ContainsKey(symbol.Name))
            {
                return false;
            }

            _symbols ??= new Dictionary<string, Symbol>();
            _symbols.Add(symbol.Name, symbol);

            return true;
        }

        /// <summary>声明函数：同名不同签名（重载）放行；同签名（参数类型逐一相同）拒绝。</summary>
        public bool TryDeclareFunction(FunctionSymbol function)
        {
            if (_symbols != null && _symbols.ContainsKey(function.Name))
            {
                return false;
            }

            if (_functions == null)
            {
                _functions = new Dictionary<string, List<FunctionSymbol>>();
            }
            else if (_functions.TryGetValue(function.Name, out var existing) && existing.Any(f => SameSignature(f, function)))
            {
                return false;
            }

            if (!_functions.TryGetValue(function.Name, out var list))
            {
                list = new List<FunctionSymbol>();
                _functions.Add(function.Name, list);
            }

            list.Add(function);

            return true;
        }

        /// <summary>声明命名空间函数：按 `ns.name` 键，同名同签名拒绝。</summary>
        public bool TryDeclareNamespaceFunction(string @namespace, FunctionSymbol function)
        {
            var key = @namespace.Length == 0 ? function.Name : @namespace + "." + function.Name;

            if (_namespaceFunctions == null)
            {
                _namespaceFunctions = new Dictionary<string, List<FunctionSymbol>>();
            }
            else if (_namespaceFunctions.TryGetValue(key, out var existing) && existing.Any(f => SameSignature(f, function)))
            {
                return false;
            }

            if (!_namespaceFunctions.TryGetValue(key, out var list))
            {
                list = new List<FunctionSymbol>();
                _namespaceFunctions.Add(key, list);
            }

            list.Add(function);

            return true;
        }

        /// <summary>按名查符号（函数取首候选，兼容非调用上下文）。</summary>
        public Symbol? TryLookupSymbol(string name)
        {
            if (_functions != null && _functions.TryGetValue(name, out var functions))
            {
                return functions[0];
            }

            if (_symbols != null && _symbols.TryGetValue(name, out var symbol))
            {
                return symbol;
            }

            return Parent?.TryLookupSymbol(name);
        }

        /// <summary>按名查全部函数候选（重载），沿作用域链；被同名非函数符号遮蔽返回空集；无匹配返回 null。</summary>
        public ImmutableArray<FunctionSymbol>? TryLookupFunctions(string name)
        {
            if (_symbols != null && _symbols.ContainsKey(name))
            {
                // 本作用域同名非函数符号（变量/类型）遮蔽 → 无函数候选
                return ImmutableArray<FunctionSymbol>.Empty;
            }

            if (_functions != null && _functions.TryGetValue(name, out var functions))
            {
                return functions.ToImmutableArray();
            }

            return Parent?.TryLookupFunctions(name);
        }

        /// <summary>按命名空间+函数名查全部候选（重载），沿作用域链；无匹配返回 null。</summary>
        public ImmutableArray<FunctionSymbol>? TryLookupNamespaceFunctions(string @namespace, string name)
        {
            var key = @namespace.Length == 0 ? name : @namespace + "." + name;

            if (_namespaceFunctions != null && _namespaceFunctions.TryGetValue(key, out var functions))
            {
                return functions.ToImmutableArray();
            }

            return Parent?.TryLookupNamespaceFunctions(@namespace, name);
        }

        private static bool SameSignature(FunctionSymbol a, FunctionSymbol b)
        {
            if (a.Parameters.Length != b.Parameters.Length)
            {
                return false;
            }

            for (var i = 0; i < a.Parameters.Length; i++)
            {
                if (a.Parameters[i].Type != b.Parameters[i].Type ||
                    a.Parameters[i].IsOut != b.Parameters[i].IsOut ||
                    a.Parameters[i].IsRef != b.Parameters[i].IsRef)
                {
                    return false;
                }
            }

            return true;
        }

        public ImmutableArray<VariableSymbol> GetDeclaredVariables() => GetDeclaredSymbols<VariableSymbol>();

        public ImmutableArray<NamedTypeSymbol> GetDeclaredEnums() => GetDeclaredSymbols<NamedTypeSymbol>();

        public ImmutableArray<NamedTypeSymbol> GetDeclaredClasses() => GetDeclaredSymbols<NamedTypeSymbol>();

        public ImmutableArray<FunctionSymbol> GetDeclaredFunctions()
        {
            if (_functions == null)
            {
                return ImmutableArray<FunctionSymbol>.Empty;
            }

            var builder = ImmutableArray.CreateBuilder<FunctionSymbol>();
            foreach (var list in _functions.Values)
            {
                foreach (var function in list)
                {
                    builder.Add(function);
                }
            }

            return builder.ToImmutable();
        }

        private ImmutableArray<TSymbol> GetDeclaredSymbols<TSymbol>() where TSymbol : Symbol
        {
            if (_symbols == null)
            {
                return ImmutableArray<TSymbol>.Empty;
            }

            return _symbols.Values.OfType<TSymbol>().ToImmutableArray();
        }
    }
}
