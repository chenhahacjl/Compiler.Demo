using Cocoa.CodeAnalysis.Bound;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.Text;
using System;
using System.Collections.Generic;

namespace Cocoa.CodeGen.Interpreter
{
    /// <summary>
    /// 执行钩子（3.5）：<see cref="Evaluator"/> 在调试/插桩需要时注入；生产路径为 null，零开销。
    /// 回调均在求值线程同步触发。DebuggerSession 实现本接口并经 <see cref="IExecutionHost"/> 只读视图
    /// 访问帧/局部——不再引用 Evaluator 任何 internal 成员。
    /// <paramref name="snapshotFactory"/> 为惰性快照提供者：仅在需要物化调用栈/局部变量时调用，
    /// 避免逐语句边界构建开销。
    /// </summary>
    public interface IExecutionHook
    {
        /// <summary>函数/脚本进入（帧压栈后触发）。</summary>
        void OnFunctionEnter(FunctionSymbol function) { }

        /// <summary>语句边界（序列点或每语句）。frameCount 为当前调用栈深度（廉价值）；需要时经 snapshotFactory 取完整快照。</summary>
        void OnStatementBoundary(BoundStatement statement, TextLocation? location, bool isSequencePoint, int frameCount, Func<ExecutionSnapshot> snapshotFactory) { }

        /// <summary>函数/脚本退出（帧弹栈后触发）。</summary>
        void OnFunctionExit(FunctionSymbol function) { }
    }
}