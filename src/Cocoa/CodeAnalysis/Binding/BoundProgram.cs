using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundProgram
    {
        public BoundProgram(BoundProgram? previous, ImmutableArray<Diagnostic> diagnostics, FunctionSymbol? mainFunction, FunctionSymbol? scriptFunction, ImmutableDictionary<FunctionSymbol, BoundBlockStatement> functions, ImmutableArray<ClassTypeSymbol> classes)
        {
            Previous = previous;
            Diagnostics = diagnostics;
            MainFunction = mainFunction;
            ScriptFunction = scriptFunction;
            Functions = functions;
            Classes = classes;
        }

        public BoundProgram? Previous { get; }
        public ImmutableArray<Diagnostic> Diagnostics { get; }
        public FunctionSymbol? MainFunction { get; }
        public FunctionSymbol? ScriptFunction { get; }
        public ImmutableDictionary<FunctionSymbol, BoundBlockStatement> Functions { get; }
        public ImmutableArray<ClassTypeSymbol> Classes { get; }
    }
}