using System;
using System.Collections.Generic;

namespace Cocoa.Metadata
{
    /// <summary>我们自己的属性定义（Property 表行 + MethodSemantics）。</summary>
    public sealed class IlPropertyDef
    {
        public IlPropertyDef(string name, IlType type, IlMethodDef? getter, IlMethodDef? setter)
        {
            Name = name;
            Type = type;
            Getter = getter;
            Setter = setter;
        }

        public string Name { get; }
        public IlType Type { get; }
        public IlMethodDef? Getter { get; }
        public IlMethodDef? Setter { get; }
    }
}
