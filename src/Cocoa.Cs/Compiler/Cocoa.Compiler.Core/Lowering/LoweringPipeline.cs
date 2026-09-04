using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Text;

namespace Cocoa.CodeAnalysis.Lowering
{
    /// <summary>
    /// Lowering 流水线入口（B-2 形式化）：绑定（<see cref="Binder"/>）产出原始函数体 →
    /// 本阶段统一降为 goto/CFG 形态（<see cref="Lowerer"/>）→ 全路径返回校验 →
    /// 双后端（IL/原生）与求值器经 <c>BoundProgram.Functions</c> 统一消费 lowered 树。
    /// 明确赋值分析（<see cref="DefiniteAssignmentAnalysis"/>）由调用方按需执行。
    /// </summary>
    public static class LoweringPipeline
    {
        /// <summary>对单函数体执行 Lowering；<paramref name="returnCheckLocation"/> 非空时做全路径返回校验。</summary>
        public static BoundBlockStatement Lower(
            FunctionSymbol function,
            BoundStatement body,
            DiagnosticBag diagnostics,
            TextLocation? returnCheckLocation)
        {
            var lowered = Lowerer.Lower(function, body);

            if (returnCheckLocation != null && !ControlFlowGraph.AllPathsReturn(lowered))
            {
                diagnostics.ReportAllPathsMustReturn(returnCheckLocation.Value);
            }

            return lowered;
        }
    }
}