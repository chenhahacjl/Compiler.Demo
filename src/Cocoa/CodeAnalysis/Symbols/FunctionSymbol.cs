using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Symbols
{
    public sealed class FunctionSymbol : Symbol
    {
        internal FunctionSymbol(string name, ImmutableArray<ParameterSymbol> parameters, TypeSymbol returnType, FunctionDeclarationSyntax? declaration = null)
            : base(name)
        {
            Parameters = parameters;
            ReturnType = returnType;
            Declaration = declaration;
        }

        public override SymbolKind Kind => SymbolKind.Function;

        public ImmutableArray<ParameterSymbol> Parameters { get; }
        public TypeSymbol ReturnType { get; }
        public FunctionDeclarationSyntax? Declaration { get; }
    }
}
