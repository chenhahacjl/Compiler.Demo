using System;
using System.Buffers.Binary;
using System.Text;

namespace Cocoa.CodeGen.PE
{
    public readonly record struct PeExportEntry(string Name, uint Rva, bool IsForwarder)
    {
    }
}
