using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.CSharp.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;
using CSharpBinderImpl = global::Cocoa.CodeAnalysis.CSharp.Binding.CSharpBinder;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// C# 方言编译对象（Y §6.7 A0 + S-4.2/4.3 分家：随语言库落位，经 <see cref="CSharpBinder"/> 驱动绑定）。
    /// 对标 Roslyn <c>CSharpCompilation : Compilation</c>。
    /// </summary>
    public sealed class CSharpCompilation : Compilation
    {
        public override Language Language => Language.CSharp;

        internal CSharpCompilation(bool isScript, Compilation? previous, string entryPointName, string[]? references, bool linkCodDynamically, SyntaxTree[] syntaxTrees)
            : base(isScript, previous, entryPointName, references, linkCodDynamically, syntaxTrees)
        {
        }

        internal override BoundGlobalScope BindGlobalScope(bool isScript, BoundGlobalScope? previous, ImmutableArray<SyntaxTree> syntaxTrees, string entryPointName, string[]? references, ImmutableArray<CoaProgram> codLibraries)
            => CSharpBinderImpl.BindGlobalScope(isScript, previous, syntaxTrees, entryPointName, references, codLibraries);

        internal override BoundProgram BindProgram(bool isScript, BoundProgram? previous, BoundGlobalScope globalScope, ImmutableArray<CoaProgram> codLibraries, Language dialect, bool linkCodDynamically, NamespaceSymbol? globalNamespace)
            => CSharpBinderImpl.BindProgram(isScript, previous, globalScope, codLibraries, dialect, linkCodDynamically, globalNamespace);
    }
}