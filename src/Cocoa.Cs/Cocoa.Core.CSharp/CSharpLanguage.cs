using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.CSharp.Syntax;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// C# 方言语言（M2 设计 X）：位于独立程序集 Cocoa.Core.CSharp，核心零改动即可挂载。
    /// 内建类型原名映射（int/long/short/.../float/double；与 CO 的简写表（Cocoa.Core.Cocoa 的
    /// CocoaLanguage）解耦为两套词汇，同一 TypeSymbol）。实例经 <see cref="Language"/> 注册表暴露（"csharp"），
    /// 由 <see cref="Syntax.SyntaxTree.Load"/>（.cs 扩展名）/ <c>ParseCs</c> 消费。
    /// </summary>
    public sealed class CSharpLanguage : Language
    {
        public static readonly CSharpLanguage Instance = new CSharpLanguage();

        private CSharpLanguage()
            : base("csharp")
        {
        }

        /// <summary>`.cs` 参数为类型前置 `int x`（参数绿往返源序化依据）。</summary>
        public override bool ParametersAreTypeFirst => true;

        /// <summary>
        /// 关键字识别（P1-A 词法分家）：C# 表 = 共享全表减去 CO 独占词。
        /// CO 独占关键字（function/let/property/constructor/extends/facade/syscall/import/to/step/cdecl/stdcall）
        /// 在 `.cs` 中回落为 <see cref="SyntaxKind.IdentifierToken"/>（文档 Phase 3：CO 词在 C# 可作标识符，反之亦然）。
        /// </summary>
        public override SyntaxKind GetKeywordKind(string text)
        {
            var kind = base.GetKeywordKind(text);
            return SyntaxKindLanguageOwnership.Ownership(kind) == SyntaxLanguageOwnership.CocoaOnly
                ? SyntaxKind.IdentifierToken
                : kind;
        }

        protected override TypeSymbol? LookupSpecificBuiltinType(string name) => name switch
        {
            "int" => TypeSymbol.Int32,
            "long" => TypeSymbol.Int64,
            "short" => TypeSymbol.Int16,
            "ushort" => TypeSymbol.UInt16,
            "uint" => TypeSymbol.UInt32,
            "ulong" => TypeSymbol.UInt64,
            "sbyte" => TypeSymbol.Int8,
            "byte" => TypeSymbol.UInt8,
            "float" => TypeSymbol.Float,
            "double" => TypeSymbol.Double,
            _ => null,
        };

        internal override IParser CreateParser(SyntaxTree syntaxTree) => new CSharpParser(syntaxTree);

        internal override IParser CreateParser(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> tokens)
            => new CSharpParser(syntaxTree, tokens);

        /// <summary>C# 词法分析器（S-2 Lexer 分家：C# 专属词法逻辑随语言库落位）。</summary>
        internal override ILexer CreateLexer(SyntaxTree syntaxTree)
            => new CSharpLexer(syntaxTree);

        internal override ILexer CreateLexer(SyntaxTree syntaxTree, int start)
            => new CSharpLexer(syntaxTree, start);

        /// <summary>C# 编译对象（S-4.2 Compilation 分家：CSharpCompilation 随语言库落位）。</summary>
        internal override Compilation CreateCompilation(bool isScript, Compilation? previous, string entryPointName, string[]? references, bool linkCodDynamically, SyntaxTree[] syntaxTrees)
            => new CSharpCompilation(isScript, previous, entryPointName, references, linkCodDynamically, syntaxTrees);

        /// <summary>C# 绑定器（S-4.3c 分派：返回语言库独立副本，Core 经 <see cref="IBinder"/> 窄接口消费）。</summary>
        internal override IBinder CreateBinder(bool isScript, Binding.BoundScope? parent, Symbols.FunctionSymbol? function, System.Collections.Immutable.ImmutableArray<string> references, System.Collections.Immutable.ImmutableArray<string> usingNamespaces, Func<string, TypeSymbol?> builtinTypeResolver, System.Collections.Immutable.ImmutableArray<string> usingStatics = default, System.Collections.Immutable.ImmutableDictionary<string, string> usingAliases = null, System.Collections.Immutable.ImmutableArray<Coa.CoaProgram> codLibraries = default, Symbols.NamespaceSymbol? globalNamespace = null)
            => new global::Cocoa.CodeAnalysis.CSharp.Binding.CSharpBinder(isScript, parent, function, references, usingNamespaces, builtinTypeResolver, usingStatics, usingAliases, codLibraries, globalNamespace);

        internal override (Binding.BoundBlockStatement Body, ImmutableArray<Diagnostic> Diagnostics) BuildFunctionBodyForMonomorphization(bool isScript, Binding.BoundScope parentScope, Symbols.FunctionSymbol function, Binding.BoundGlobalScope globalScope, System.Collections.Immutable.ImmutableArray<Coa.CoaProgram> codLibraries, Dictionary<string, TypeSymbol> typeArgumentsByName)
            => global::Cocoa.CodeAnalysis.CSharp.Binding.CSharpBinder.BuildFunctionBodyForMonomorphization(isScript, parentScope, function, globalScope, codLibraries, this, typeArgumentsByName);

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

        /// <summary>语义模型（P1-3 钩子：P1 返回共享 SemanticModel，P1-5 切 CSharpSemanticModel）。</summary>
        internal override SemanticModel CreateSemanticModel(Compilation compilation, SyntaxTree syntaxTree)
            => new SemanticModel(compilation, syntaxTree);

        /// <summary>不可达代码位置（P1-3 钩子：P1 委托共享解析器，P2-5 切语言节点）。</summary>
        internal override TextLocation? GetUnreachableCodeLocation(SyntaxNode node)
            => UnreachableCodeLocator.GetLocation(node);
    }
}