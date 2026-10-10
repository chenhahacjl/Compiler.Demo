using System.Collections.Generic;

namespace Cocoa.CodeGen.Interpreter
{
    /// <summary>一个调用栈帧快照（顶帧在列表首位）。</summary>
    public sealed record StackFrame(string Function, string? FilePath, int Line, IReadOnlyList<LocalVariable> Locals);
}