using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeGen.ObjectFormat.PE
{
    public sealed record PeResourceLeaf(uint Rva, uint Size);
}
