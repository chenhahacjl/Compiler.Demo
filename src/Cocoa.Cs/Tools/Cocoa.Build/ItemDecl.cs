using System.Collections.Immutable;

namespace Cocoa.Build
{
    /// <summary>单个声明项（`Source`/`Reference`/`Import`/`Content`）。</summary>
    public readonly record struct ItemDecl(string Name, string Include, ImmutableArray<(string Key, string Value)> Attributes);
}