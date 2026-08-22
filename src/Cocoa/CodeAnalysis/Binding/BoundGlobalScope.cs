using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundGlobalScope
    {
        public BoundGlobalScope(BoundGlobalScope? previous, ImmutableArray<Diagnostic> diagnostics, FunctionSymbol? mainFunction, FunctionSymbol? scriptFunction, ImmutableArray<FunctionSymbol> functions, ImmutableArray<EnumTypeSymbol> enums, ImmutableArray<ClassTypeSymbol> classes, ImmutableArray<VariableSymbol> variables, ImmutableArray<BoundStatement> statements, ImmutableArray<string> usingNamespaces, ImmutableArray<string> usingStatics, ImmutableDictionary<string, string> usingAliases, ImmutableArray<string> references)
        {
            Previous = previous;
            Diagnostics = diagnostics;
            MainFunction = mainFunction;
            ScriptFunction = scriptFunction;
            Functions = functions;
            Enums = enums;
            Classes = classes;
            Variables = variables;
            Statements = statements;
            UsingNamespaces = usingNamespaces;
            UsingStatics = usingStatics;
            UsingAliases = usingAliases;
            References = references;
        }

        public BoundGlobalScope? Previous { get; }
        public ImmutableArray<Diagnostic> Diagnostics { get; }
        public FunctionSymbol? MainFunction { get; }
        public FunctionSymbol? ScriptFunction { get; }
        public ImmutableArray<FunctionSymbol> Functions { get; }
        public ImmutableArray<EnumTypeSymbol> Enums { get; }
        public ImmutableArray<ClassTypeSymbol> Classes { get; }
        public ImmutableArray<VariableSymbol> Variables { get; }
        public ImmutableArray<BoundStatement> Statements { get; }
        public ImmutableArray<string> UsingNamespaces { get; }
        public ImmutableArray<string> UsingStatics { get; }
        public ImmutableDictionary<string, string> UsingAliases { get; }
        public ImmutableArray<string> References { get; }
    }
}
