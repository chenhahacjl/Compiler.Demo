using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// C# 方言绑定器（P1-B 分叉前置骨架：经 <see cref="CSharpLanguage.CreateBinder"/> 工厂分派）。
    /// 当前行为等价于共享 <see cref="Binder"/>——C# 专属绑定语义（C-style for 脱糖等）
    /// 随 Binder 分叉逐步落位（后续阶段把 C# 专属分支从基类下沉到本类）。
    /// </summary>
    internal sealed class CSharpBinder : Binder
    {
        public CSharpBinder(bool isScript, BoundScope? parent, FunctionSymbol? function, System.Collections.Immutable.ImmutableArray<string> references, System.Collections.Immutable.ImmutableArray<string> usingNamespaces, ImmutableArray<string> usingStatics = default, System.Collections.Immutable.ImmutableDictionary<string, string> usingAliases = null, System.Collections.Immutable.ImmutableArray<Coa.CoaProgram> codLibraries = default, NamespaceSymbol? globalNamespace = null)
            : base(isScript, parent, function, references, usingNamespaces, CSharpLanguage.Instance.LookupBuiltinType, usingStatics, usingAliases, codLibraries, globalNamespace)
        {
        }
    }
}