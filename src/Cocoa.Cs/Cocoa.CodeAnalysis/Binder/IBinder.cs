using Cocoa.CodeAnalysis.CocoaAssembly;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定器窄接口（S-4.3b）：Core 共享 HIR 服务（<see cref="Monomorphizer"/>）所需的最小绑定面，
    /// 经 <see cref="Language.CreateBinder"/> 返回；共享 Binder 与语言库副本均实现。
    /// 完整实例绑定逻辑随语言库落位（CocoaBinder / CSharpBinder），本接口仅暴露单态化展开需要的方法。
    /// </summary>
    internal interface IBinder
    {
        void RegisterSourceGenericDefinitionsForSeed(BoundGlobalScope globalScope);
        void RegisterCodGenericDefinitionsForSeed(ImmutableArray<CoaProgram> libraries);
        TypeSymbol? BindGenericTypeNameForExpansion(SyntaxToken identifier, ImmutableArray<SyntaxNode> argumentClauses);
    }
}
