using System;
using System.Collections.Generic;

namespace Cocoa.Metadata
{
    /// <summary>我们自己的字段定义（FieldDef 表行）。</summary>
    public sealed class IlFieldDef
    {
        public IlFieldDef(string name, IlType type, IlVisibility visibility, bool isStatic = false)
        {
            Name = name;
            Type = type;
            Visibility = visibility;
            IsStatic = isStatic;
        }

        public string Name { get; }
        public IlType Type { get; }
        public IlVisibility Visibility { get; }
        public bool IsStatic { get; }
    }
}
