using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundProgram
    {
        public BoundProgram(BoundProgram? previous, ImmutableArray<Diagnostic> diagnostics, FunctionSymbol? mainFunction, FunctionSymbol? scriptFunction, ImmutableDictionary<FunctionSymbol, BoundBlockStatement> functions, ImmutableArray<ClassTypeSymbol> classes, ImmutableDictionary<object, string>? codAssemblies = null)
        {
            Previous = previous;
            Diagnostics = diagnostics;
            MainFunction = mainFunction;
            ScriptFunction = scriptFunction;
            Functions = functions;
            Classes = classes;
            CodAssemblies = codAssemblies ?? ImmutableDictionary<object, string>.Empty;
        }

        public BoundProgram? Previous { get; }
        public ImmutableArray<Diagnostic> Diagnostics { get; }
        public FunctionSymbol? MainFunction { get; }
        public FunctionSymbol? ScriptFunction { get; }
        public ImmutableDictionary<FunctionSymbol, BoundBlockStatement> Functions { get; }
        public ImmutableArray<ClassTypeSymbol> Classes { get; }

        /// <summary>
        /// 动态链接（阶段 A2）：cod 来源符号 → 所属库程序集名（如 "MyLib"）。
        /// 非空时 IlEmitter 对这些符号合成 AssemblyRef/TypeRef/MemberRef 指向同名 dll，
        /// 而非内联实现；键为 ClassTypeSymbol / FunctionSymbol（extern 与 builtin 单例不入表）。
        /// </summary>
        public ImmutableDictionary<object, string> CodAssemblies { get; }
    }
}