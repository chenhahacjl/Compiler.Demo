using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    /// <summary>资源树节点（解析产物）。</summary>
    public sealed record PeResourceNode(string Name, uint Id, IReadOnlyList<PeResourceNode> Children, PeResourceLeaf? Leaf);
}
