using System.Collections.Immutable;
using Cocoa.CodeAnalysis;

namespace Cocoa.Engine
{
    /// <summary>单次脚本提交的执行结果。</summary>
    /// <param name="Diagnostics">本次提交的编译诊断（语法/绑定/发射），警告照常返回。</param>
    /// <param name="Value">脚本顶层最后表达式的值（与 REPL 语义一致；无表达式为 null）。</param>
    public sealed record EngineResult(ImmutableArray<Diagnostic> Diagnostics, object? Value);
}