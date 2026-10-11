using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Generic;

namespace Cocoa.CodeGen.Interpreter
{
    /// <summary>
    /// 调试/插桩所需的只读执行视图（3.5）：DebuggerSession 仅经本接口访问求值器状态，
    /// 不再引用 <see cref="Evaluator"/> 任何 internal 成员。Evaluator 实现本接口。
    /// </summary>
    public interface IExecutionHost
    {
        /// <summary>当前注入的执行钩子（可为 null，无调试时零开销）。</summary>
        IExecutionHook? ExecutionHook { get; set; }

        /// <summary>求值入口函数（Main/Script）。</summary>
        object? Evaluate(string[]? args);

        /// <summary>构建当前执行状态只读快照（调用栈 + 顶帧局部）。在暂停/语句边界调用均安全。</summary>
        ExecutionSnapshot BuildSnapshot();
    }

    /// <summary>执行状态只读快照（3.5）：调用栈（顶帧在前）+ 顶帧局部变量，全部为公开 record，隔离内部类型。</summary>
    public sealed class ExecutionSnapshot
    {
        public ExecutionSnapshot(IReadOnlyList<StackFrame> callStack, IReadOnlyList<LocalVariable> topLocals)
        {
            CallStack = callStack;
            TopLocals = topLocals;
        }

        /// <summary>调用栈（顶帧在列表首位）。</summary>
        public IReadOnlyList<StackFrame> CallStack { get; }

        /// <summary>顶帧局部变量（含全局变量合并）。</summary>
        public IReadOnlyList<LocalVariable> TopLocals { get; }

        /// <summary>调用栈帧数。</summary>
        public int FrameCount => CallStack.Count;
    }
}