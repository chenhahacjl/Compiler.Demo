using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Cocoa.CodeGen.PE
{
    public sealed record PeRelocationBlock(uint PageRva, IReadOnlyList<PeRelocationEntry> Entries);
}
