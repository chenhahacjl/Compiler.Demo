using System;
using System.Collections.Immutable;
using System.IO;

namespace Cocoa.Build
{
    /// <summary>解析产物：保留分组/Label/Condition/顺序的条件化项目模型。</summary>
    public sealed class CocoaProjectSpec
    {
        public CocoaProjectSpec(
            string filePath,
            ImmutableArray<PropertyGroupDecl> propertyGroups,
            ImmutableArray<ItemGroupDecl> itemGroups)
        {
            FilePath = Path.GetFullPath(filePath);
            PropertyGroups = propertyGroups;
            ItemGroups = itemGroups;
        }

        public string FilePath { get; }
        public ImmutableArray<PropertyGroupDecl> PropertyGroups { get; }
        public ImmutableArray<ItemGroupDecl> ItemGroups { get; }
    }
}