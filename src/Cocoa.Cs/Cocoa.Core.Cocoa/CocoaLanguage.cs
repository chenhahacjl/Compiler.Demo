using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Cocoa.Syntax;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// Cocoa 宿主语言（默认，`.co`；Y-A3-4 随 CO L1 迁入独立程序集 Cocoa.Core.Cocoa）：
    /// 内建类型简写 i8/u8/i16/u16/i32/u32/i64/u64/f32/f64（+ i128/u128/f128 占位；C# 原名映射见
    /// <see cref="Cocoa.CodeAnalysis.CSharpLanguage"/>）。实例经 <see cref="Language"/> 注册表暴露（"cocoa"），
    /// 由 <see cref="Syntax.SyntaxTree.Parse(string)"/>（默认）/ <c>Parse(text, Language.Cocoa)</c> 消费。
    /// 核心（Cocoa.Core）经 <see cref="Language.Cocoa"/> 反射装载本程序集并触达 <see cref="Instance"/>，
    /// 以维持"核心即 CO 工具链本体"的默认解析路径，同时规避 Cocoa.Core↔Cocoa.Core.Cocoa 循环依赖。
    /// </summary>
    public sealed class CocoaLanguage : Language
    {
        public static readonly CocoaLanguage Instance = new CocoaLanguage();

        private CocoaLanguage()
            : base("cocoa")
        {
        }

        protected override TypeSymbol? LookupSpecificBuiltinType(string name) => name switch
        {
            "i8" => TypeSymbol.Int8,
            "u8" => TypeSymbol.UInt8,
            "i16" => TypeSymbol.Int16,
            "u16" => TypeSymbol.UInt16,
            "i32" => TypeSymbol.Int32,
            "u32" => TypeSymbol.UInt32,
            "i64" => TypeSymbol.Int64,
            "u64" => TypeSymbol.UInt64,
            "f32" => TypeSymbol.Float,
            "f64" => TypeSymbol.Double,
            "i128" => TypeSymbol.Int128,
            "u128" => TypeSymbol.UInt128,
            "f128" => TypeSymbol.Float128,
            _ => null,
        };

        /// <summary>CO 绑定器（S-4.3c 分派：返回语言库独立副本，Core 经 <see cref="IBinder"/> 窄接口消费）。</summary>
        internal override IBinder CreateBinder(bool isScript, Binding.BoundScope? parent, Symbols.FunctionSymbol? function, System.Collections.Immutable.ImmutableArray<string> references, System.Collections.Immutable.ImmutableArray<string> usingNamespaces, Func<string, TypeSymbol?> builtinTypeResolver, System.Collections.Immutable.ImmutableArray<string> usingStatics = default, System.Collections.Immutable.ImmutableDictionary<string, string> usingAliases = null, System.Collections.Immutable.ImmutableArray<Coa.CoaProgram> codLibraries = default, Symbols.NamespaceSymbol? globalNamespace = null)
            => new global::Cocoa.CodeAnalysis.Cocoa.Binding.CocoaBinder(isScript, parent, function, references, usingNamespaces, builtinTypeResolver, usingStatics, usingAliases, codLibraries, globalNamespace);

        internal override (Binding.BoundBlockStatement Body, ImmutableArray<Diagnostic> Diagnostics) BuildFunctionBodyForMonomorphization(bool isScript, Binding.BoundScope parentScope, Symbols.FunctionSymbol function, Binding.BoundGlobalScope globalScope, System.Collections.Immutable.ImmutableArray<Coa.CoaProgram> codLibraries, Dictionary<string, TypeSymbol> typeArgumentsByName)
            => global::Cocoa.CodeAnalysis.Cocoa.Binding.CocoaBinder.BuildFunctionBodyForMonomorphization(isScript, parentScope, function, globalScope, codLibraries, this, typeArgumentsByName);

        internal override IParser CreateParser(SyntaxTree syntaxTree) => new CocoaParser(syntaxTree);

        internal override IParser CreateParser(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> tokens)
            => new CocoaParser(syntaxTree, tokens);

        /// <summary>CO 词法分析器（S-2 Lexer 分家：CO 专属词法逻辑随语言库落位）。</summary>
        internal override ILexer CreateLexer(SyntaxTree syntaxTree)
            => new CocoaLexer(syntaxTree);

        internal override ILexer CreateLexer(SyntaxTree syntaxTree, int start)
            => new CocoaLexer(syntaxTree, start);

        /// <summary>CO 编译对象（S-4.2 Compilation 分家：CocoaCompilation 随语言库落位）。</summary>
        internal override Compilation CreateCompilation(bool isScript, Compilation? previous, string entryPointName, string[]? references, bool linkCodDynamically, SyntaxTree[] syntaxTrees)
            => new CocoaCompilation(isScript, previous, entryPointName, references, linkCodDynamically, syntaxTrees);

        /// <summary>绿→类型化红节点（P1-3 钩子：P1 委托共享 <see cref="GreenNode.CreateTypedRed"/>，P2-4 切语言节点）。</summary>
        internal override SyntaxNode CreateTypedRed(GreenNode green, SyntaxTree syntaxTree, int position)
            => green.CreateTypedRed(syntaxTree, position);

        /// <summary>泛型用法扫描（P1-3 钩子：P1 委托共享 Monomorphizer 扫描，P2-5 切语言节点）。</summary>
        internal override System.Collections.Generic.IEnumerable<(SyntaxToken Identifier, System.Collections.Immutable.ImmutableArray<SyntaxNode> Arguments)> CollectGenericUsages(Binding.BoundGlobalScope globalScope)
            => Monomorphizer.CollectGenericUsages(globalScope);

        /// <summary>声明的命名空间名集合（P1-3 钩子：P1 委托共享服务，P2-5 切语言节点）。</summary>
        internal override System.Collections.Immutable.ImmutableArray<string> GetDeclaredNamespaceNames(SyntaxTree syntaxTree)
            => SyntaxTreeServices.GetDeclaredNamespaceNames(syntaxTree);

        /// <summary>根成员集合（P1-3 钩子：P1 委托共享服务，P2-5 切语言节点）。</summary>
        internal override System.Collections.Immutable.ImmutableArray<SyntaxNode> GetRootMembers(SyntaxTree syntaxTree)
            => SyntaxTreeServices.GetRootMembers(syntaxTree);

        /// <summary>语义模型（P1-3 钩子：P1 返回共享 SemanticModel，P1-5 切 CocoaSemanticModel）。</summary>
        internal override SemanticModel CreateSemanticModel(Compilation compilation, SyntaxTree syntaxTree)
            => new SemanticModel(compilation, syntaxTree);

        /// <summary>不可达代码位置（P1-3 钩子：P1 委托共享解析器，P2-5 切语言节点）。</summary>
        internal override TextLocation? GetUnreachableCodeLocation(SyntaxNode node)
            => UnreachableCodeLocator.GetLocation(node);
    }
}
