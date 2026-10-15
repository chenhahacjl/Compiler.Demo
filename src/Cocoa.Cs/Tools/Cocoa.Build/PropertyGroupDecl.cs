using System.Collections.Immutable;

namespace Cocoa.Build
{
    /// <summary>声明的属性组（含 Label 与 Condition 文本）。</summary>
    public readonly record struct PropertyGroupDecl(string Label, string? Condition, ImmutableArray<PropertyValue> Values);
}