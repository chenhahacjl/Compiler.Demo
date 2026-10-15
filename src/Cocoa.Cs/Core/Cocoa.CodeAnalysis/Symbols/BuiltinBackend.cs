using System;

namespace Cocoa.CodeAnalysis.Symbols
{
    [Flags]
    public enum BuiltinBackend
    {
        None = 0,
        Evaluator = 1 << 0,
        Il = 1 << 1,
        Native = 1 << 2,
        All = Evaluator | Il | Native,
    }
}