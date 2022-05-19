using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定节点
    /// </summary>
    internal abstract class BoundNode
    {
        public abstract BoundNodeKind Kind { get; }

        public override string ToString()
        {
            using (var writer = new StringWriter())
            {
                this.WriteTo(writer);

                return writer.ToString();
            }
        }
    }
}
