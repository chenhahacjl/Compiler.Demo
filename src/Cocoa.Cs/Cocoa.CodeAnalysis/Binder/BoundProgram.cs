using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundProgram
    {
        public BoundProgram(BoundProgram? previous, ImmutableArray<Diagnostic> diagnostics, FunctionSymbol? mainFunction, FunctionSymbol? scriptFunction, ImmutableDictionary<FunctionSymbol, BoundBlockStatement> functions, ImmutableArray<NamedTypeSymbol> classes, ImmutableDictionary<object, string>? codAssemblies = null, ImmutableArray<NamedTypeSymbol>? genericDefinitions = null, ImmutableDictionary<FunctionSymbol, BoundBlockStatement>? genericOpenBodies = null, ImmutableDictionary<FunctionSymbol, BoundBlockStatement>? rawFunctions = null)
        {
            Previous = previous;
            Diagnostics = diagnostics;
            MainFunction = mainFunction;
            ScriptFunction = scriptFunction;
            Functions = functions;
            Classes = classes;
            CodAssemblies = codAssemblies ?? ImmutableDictionary<object, string>.Empty;
            GenericDefinitions = genericDefinitions ?? ImmutableArray<NamedTypeSymbol>.Empty;
            GenericOpenBodies = genericOpenBodies ?? ImmutableDictionary<FunctionSymbol, BoundBlockStatement>.Empty;
            RawFunctions = rawFunctions ?? ImmutableDictionary<FunctionSymbol, BoundBlockStatement>.Empty;
        }

        public BoundProgram? Previous { get; }
        public ImmutableArray<Diagnostic> Diagnostics { get; }
        public FunctionSymbol? MainFunction { get; }
        public FunctionSymbol? ScriptFunction { get; }
        public ImmutableDictionary<FunctionSymbol, BoundBlockStatement> Functions { get; }
        public ImmutableArray<NamedTypeSymbol> Classes { get; }

        /// <summary>
        /// 动态链接（阶段 A2）：cod 来源符号 → 所属库程序集名（如 "MyLib"）。
        /// 非空时 IlEmitter 对这些符号合成 AssemblyRef/TypeRef/MemberRef 指向同名 dll，
        /// 而非内联实现；键为 NamedTypeSymbol / FunctionSymbol（extern 与 builtin 单例不入表）。
        /// </summary>
        public ImmutableDictionary<object, string> CodAssemblies { get; }

        /// <summary>
        /// 泛型定义类（6e-G7 S1）：模板壳，IL/native 发射清单排除；仅 EmitCocoa 序列化为 gcls 条目。
        /// </summary>
        public ImmutableArray<NamedTypeSymbol> GenericDefinitions { get; }

        /// <summary>
        /// 泛型定义方法的开放绑定体（6e-G7 S2）：T 保持开放的降级 Bound 块，
        /// EmitCocoa 序列化进 bodies 区供消费方替换展开（S-7 后为 raw 结构化 HIR）。
        /// </summary>
        public ImmutableDictionary<FunctionSymbol, BoundBlockStatement> GenericOpenBodies { get; }

        /// <summary>
        /// 源码函数的 raw（未 Lower）绑定体（S-7）：绑定 + 插值归一后、Lowering 前，
        /// EmitCocoa 以此为 `.coa` bodies 序列化源（for/while/if 保留）。
        /// Functions（lowered/MIR）仍是三后端与求值器唯一消费契约。
        /// </summary>
        public ImmutableDictionary<FunctionSymbol, BoundBlockStatement> RawFunctions { get; }
    }
}