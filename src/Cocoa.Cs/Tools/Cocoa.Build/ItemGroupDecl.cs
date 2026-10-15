using System.Collections.Immutable;

namespace Cocoa.Build
{
    /// <summary>声明的项组（组级 Condition + 项列表）。</summary>
    public readonly record struct ItemGroupDecl(string? Condition, ImmutableArray<ItemDecl> Items);
}