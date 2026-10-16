using System;
using System.Collections.Generic;
using System.Linq;
using Cocoa.CodeAnalysis;
using Cocoa.CodeAnalysis.Bound;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;
using Cocoa.Text;
using Cocoa.CodeGen.Interpreter;
using Xunit;

namespace Cocoa.Tests.CodeGen.ThreeBackend
{
    /// <summary>
    /// 执行钩子契约（3.5）：Evaluator 无钩子 = 常规求值零开销路径；挂 <see cref="IExecutionHook"/> 时
    /// 语句边界按序列点回调，函数进入/退出配对。经公开 <see cref="IExecutionHost"/> 视图，不碰 internal。
    /// </summary>
    public class ExecutionHookTests
    {
        private static Compilation Compile(string text)
            => Compilation.CreateScript(null, SyntaxTree.Parse(SourceText.From(text, "probe.co")));

        private static IExecutionHost CreateHost(string text, IExecutionHook? hook)
        {
            var program = Compile(text).GetProgram();
            return InterpreterBackend.CreateExecutionHost(program, new Dictionary<VariableSymbol, object>(), hook);
        }

        [Fact]
        public void NoHook_Evaluates_Normally()
        {
            var host = CreateHost("var a = 1\nvar b = a + 2\nreturn b\n", hook: null);
            Assert.Equal(3, host.Evaluate(null));
        }

        [Fact]
        public void WithHook_CountsSequencePointBoundaries()
        {
            var countingHook = new CountingHook();
            var host = CreateHost("var a = 1\nvar b = a + 2\nreturn b\n", countingHook);

            var result = host.Evaluate(null);

            Assert.Equal(3, result);
            Assert.True(countingHook.BoundaryCount >= 2, $"应至少 2 个语句边界，实际 {countingHook.BoundaryCount}");
            Assert.True(countingHook.FunctionEnterCount >= 1, $"应至少 1 次函数进入，实际 {countingHook.FunctionEnterCount}");
            Assert.Equal(countingHook.FunctionEnterCount, countingHook.FunctionExitCount);
        }

        [Fact]
        public void Snapshot_ExposesCallStackAndGlobals()
        {
            var snapshotHook = new SnapshotingHook();
            var host = CreateHost("var g = 42\nvar a = g + 1\nreturn a\n", snapshotHook);

            var result = host.Evaluate(null);

            Assert.Equal(43, result);
            Assert.NotNull(snapshotHook.LastSnapshot);
            Assert.True(snapshotHook.LastSnapshot!.CallStack.Count >= 1);
            Assert.Contains(snapshotHook.LastSnapshot.TopLocals, l => l.Name == "g" || l.Name == "a");
        }

        private sealed class CountingHook : IExecutionHook
        {
            public int BoundaryCount;
            public int FunctionEnterCount;
            public int FunctionExitCount;

            public void OnFunctionEnter(FunctionSymbol function) => FunctionEnterCount++;

            public void OnStatementBoundary(BoundStatement statement, TextLocation? location, bool isSequencePoint, int frameCount, Func<ExecutionSnapshot> snapshotFactory)
            {
                if (isSequencePoint) BoundaryCount++;
            }

            public void OnFunctionExit(FunctionSymbol function) => FunctionExitCount++;
        }

        private sealed class SnapshotingHook : IExecutionHook
        {
            public ExecutionSnapshot? LastSnapshot;

            public void OnStatementBoundary(BoundStatement statement, TextLocation? location, bool isSequencePoint, int frameCount, Func<ExecutionSnapshot> snapshotFactory)
            {
                LastSnapshot = snapshotFactory();
            }
        }
    }
}