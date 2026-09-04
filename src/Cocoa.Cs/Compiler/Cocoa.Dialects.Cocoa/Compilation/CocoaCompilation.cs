using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Serialization;
using Cocoa.CodeAnalysis.Cocoa.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;
using CocoaBinderImpl = global::Cocoa.CodeAnalysis.Cocoa.Binding.CocoaBinder;

namespace Cocoa.CodeAnalysis
{
    /// <summary>
    /// CO 侧语言编译对象（Y §6.7 A0 + S-4.2/4.3 分家：随语言库落位，经 <see cref="CocoaBinder"/> 驱动绑定）。
    /// 对标 Roslyn <c>CSharpCompilation : Compilation</c>。
    /// </summary>
    public sealed class CocoaCompilation : Compilation
    {
        public override Language Language => Language.Cocoa;

        internal CocoaCompilation(bool isScript, Compilation? previous, string entryPointName, string[]? references, bool linkCodDynamically, SyntaxTree[] syntaxTrees)
            : base(isScript, previous, entryPointName, references, linkCodDynamically, syntaxTrees)
        {
        }

        public override BoundGlobalScope BindGlobalScope(bool isScript, BoundGlobalScope? previous, ImmutableArray<SyntaxTree> syntaxTrees, string entryPointName, string[]? references, ImmutableArray<CoaProgram> codLibraries)
            => CocoaBinderImpl.BindGlobalScope(isScript, previous, syntaxTrees, entryPointName, references, codLibraries);

        public override BoundProgram BindProgram(bool isScript, BoundProgram? previous, BoundGlobalScope globalScope, ImmutableArray<CoaProgram> codLibraries, Language dialect, bool linkCodDynamically, NamespaceSymbol? globalNamespace)
            => CocoaBinderImpl.BindProgram(isScript, previous, globalScope, codLibraries, dialect, linkCodDynamically, globalNamespace);
    }
}