using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// CO 语言绑定器（P1-B CO 显式化骨架：经 <see cref="CocoaLanguage.CreateBinder"/> 工厂分派）。
    /// 当前行为等价于共享 <see cref="Binder"/>——语言专属绑定语义（CO for-to/step、facade、syscall 等）
    /// 随 Binder 分叉逐步落位（后续阶段把 CO 专属分支从基类下沉到本类）。
    /// </summary>
    internal sealed class CocoaBinder : Binder
    {
        public CocoaBinder(bool isScript, BoundScope? parent, FunctionSymbol? function, System.Collections.Immutable.ImmutableArray<string> references, System.Collections.Immutable.ImmutableArray<string> usingNamespaces, ImmutableArray<string> usingStatics = default, System.Collections.Immutable.ImmutableDictionary<string, string> usingAliases = null, System.Collections.Immutable.ImmutableArray<Coa.CoaProgram> codLibraries = default, NamespaceSymbol? globalNamespace = null)
            : base(isScript, parent, function, references, usingNamespaces, CocoaLanguage.Instance.LookupBuiltinType, usingStatics, usingAliases, codLibraries, globalNamespace)
        {
        }
    }
}