using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    public sealed class BoundGlobalScope
    {
        public BoundGlobalScope(BoundGlobalScope? previous, ImmutableArray<Diagnostic> diagnostics, FunctionSymbol? mainFunction, FunctionSymbol? scriptFunction, ImmutableArray<FunctionSymbol> functions, ImmutableArray<NamedTypeSymbol> enums, ImmutableArray<NamedTypeSymbol> classes, ImmutableArray<VariableSymbol> variables, ImmutableArray<BoundStatement> statements, ImmutableArray<string> usingNamespaces, ImmutableArray<string> usingStatics, ImmutableDictionary<string, string> usingAliases, ImmutableArray<string> references)
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
        public ImmutableArray<NamedTypeSymbol> Enums { get; }
        public ImmutableArray<NamedTypeSymbol> Classes { get; }
        public ImmutableArray<VariableSymbol> Variables { get; }
        public ImmutableArray<BoundStatement> Statements { get; }
        public ImmutableArray<string> UsingNamespaces { get; }
        public ImmutableArray<string> UsingStatics { get; }
        public ImmutableDictionary<string, string> UsingAliases { get; }
        public ImmutableArray<string> References { get; }

        /// <summary>合成构造器体（元组 `__Tuple_N`）：源内无 Declaration，携入 BindProgram 并入函数体清单。</summary>
        public ImmutableDictionary<FunctionSymbol, BoundBlockStatement> TupleCtorBodies { get; init; } = ImmutableDictionary<FunctionSymbol, BoundBlockStatement>.Empty;

        /// <summary>
        /// 运算符重载查找表——跨 binder 实例共享：声明期 binder 填入，函数体绑定期（另一个 binder 实例）查询。
        /// 放在 global scope 而非 binder 实例上，是因为体绑定遍与声明遍不共用 <see cref="Binding.CocoaBinder"/> 实例。
        /// </summary>
        public Binding.OperatorRegistry Operators { get; init; } = new Binding.OperatorRegistry();
    }
}
