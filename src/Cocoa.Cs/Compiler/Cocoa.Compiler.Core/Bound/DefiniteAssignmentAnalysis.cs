using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 明确赋值分析（6e-M23 R4，对齐 C#）：跟踪当前函数 out 形参的赋值状态。
    /// 普通局部经默认值合成永远视为已初始化——唯一"未赋值"实体是 out 形参（入口契约性未赋值），
    /// 故跟踪集仅为当前函数的 out 形参集合。
    /// 规则：①出口前必须已赋值；②未赋值禁读；③作 ref 实参传递要求已赋值；
    /// ④作 out 实参传递后视为已赋值（被调方契约）。
    /// 数据流：前向 must——IN[B] = AND(OUT[pred])；转移仅 gen 无 kill；Start 块出口 = 全 false（入口契约）。
    /// 降级后函数体为扁平语句序列（标签/goto/条件 goto），CFG 与扫描均按扁平模型处理；
    /// lambda 体不在表达式子树内（BoundChildren 只含 Receiver），无需跳过。
    /// </summary>
    public static class DefiniteAssignmentAnalysis
    {
        public static void Analyze(BoundBlockStatement loweredBody, ImmutableArray<ParameterSymbol> outParameters, DiagnosticBag diagnostics)
        {
            if (outParameters.IsEmpty)
            {
                return;
            }

            var graph = ControlFlowGraph.Create(loweredBody);
            var count = outParameters.Length;

            var indexOf = new Dictionary<VariableSymbol, int>();
            for (var i = 0; i < count; i++)
            {
                indexOf[outParameters[i]] = i;
            }

            var blocks = graph.Blocks;
            var blockIndex = new Dictionary<ControlFlowGraph.BasicBlock, int>();
            for (var i = 0; i < blocks.Count; i++)
            {
                blockIndex[blocks[i]] = i;
            }

            var inStates = new bool[blocks.Count][];
            var outStates = new bool[blocks.Count][];

            for (var i = 0; i < blocks.Count; i++)
            {
                // 入口契约：Start 出口全 false；其余块以 top（全 true）初始化迭代收敛，
                // 无前驱的不可达块保持 top ⇒ 不产生误报（死代码已由 Lowerer 清理，此处兜底）
                var isStart = blocks[i].IsStart;
                inStates[i] = Repeat(!isStart, count);
                outStates[i] = Repeat(!isStart, count);
            }

            bool changed = true;
            while (changed)
            {
                changed = false;
                for (var i = 0; i < blocks.Count; i++)
                {
                    var block = blocks[i];
                    if (block.IsStart || block.IsEnd)
                    {
                        continue;
                    }

                    var newIn = MeetIncoming(block, blockIndex, outStates, inStates[i], count);
                    var newOut = Transfer(block, newIn, indexOf);

                    if (!Same(newIn, inStates[i]) || !Same(newOut, outStates[i]))
                    {
                        inStates[i] = newIn;
                        outStates[i] = newOut;
                        changed = true;
                    }
                }
            }

            // 第二遍：带诊断的语义扫描（每块从其 IN 状态出发）
            var reportingScanner = new Scanner(indexOf, diagnostics);
            for (var i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                if (block.IsStart || block.IsEnd)
                {
                    continue;
                }

                var state = (bool[])inStates[i].Clone();
                foreach (var statement in block.Statements)
                {
                    if (statement.Kind == BoundNodeKind.ReturnStatement)
                    {
                        var returnStatement = (BoundReturnStatement)statement;
                        if (returnStatement.Expression != null)
                        {
                            reportingScanner.ScanExpression(returnStatement.Expression, state);
                        }

                        foreach (var parameter in outParameters)
                        {
                            if (!state[indexOf[parameter]])
                            {
                                diagnostics.ReportOutParameterNotAssignedOnReturn(statement.Syntax.Location, parameter.Name);
                            }
                        }
                    }
                    else
                    {
                        reportingScanner.ScanStatement(statement, state);
                    }
                }

                foreach (var branch in block.Outgoing)
                {
                    if (branch.Condition != null)
                    {
                        reportingScanner.ScanExpression(branch.Condition, state);
                    }
                }
            }
        }

        private static bool[] Repeat(bool value, int count)
        {
            var array = new bool[count];
            if (value)
            {
                Array.Fill(array, true);
            }

            return array;
        }

        private static bool[] MeetIncoming(
            ControlFlowGraph.BasicBlock block,
            Dictionary<ControlFlowGraph.BasicBlock, int> blockIndex,
            bool[][] outStates,
            bool[] fallback,
            int count)
        {
            bool[]? result = null;
            foreach (var branch in block.Incoming)
            {
                var source = outStates[blockIndex[branch.From]];
                if (result == null)
                {
                    result = (bool[])source.Clone();
                }
                else
                {
                    for (var i = 0; i < count; i++)
                    {
                        result[i] &= source[i];
                    }
                }
            }

            return result ?? fallback;
        }

        private static bool[] Transfer(ControlFlowGraph.BasicBlock block, bool[] input, Dictionary<VariableSymbol, int> indexOf)
        {
            var state = (bool[])input.Clone();
            var scanner = new Scanner(indexOf, diagnostics: null);
            foreach (var statement in block.Statements)
            {
                scanner.ScanStatement(statement, state);
            }

            return state;
        }

        private static bool Same(bool[] a, bool[] b)
        {
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 语句/表达式扫描器。diagnostics 为 null 时仅做状态转移（不动点计算用），
        /// 非 null 时同时报读取类诊断。Assignment 目标不算读（BoundChildren 本就只给 RHS），
        /// CompoundAssignment 显式先读后写，ByRefArgument 按 out/ref 分派。
        /// </summary>
        private sealed class Scanner
        {
            private readonly Dictionary<VariableSymbol, int> _indexOf;
            private readonly DiagnosticBag? _diagnostics;

            public Scanner(Dictionary<VariableSymbol, int> indexOf, DiagnosticBag? diagnostics)
            {
                _indexOf = indexOf;
                _diagnostics = diagnostics;
            }

            public void ScanStatement(BoundStatement statement, bool[] state)
            {
                switch (statement.Kind)
                {
                    case BoundNodeKind.ReturnStatement:
                    {
                        var expression = ((BoundReturnStatement)statement).Expression;
                        if (expression != null)
                        {
                            ScanExpression(expression, state);
                        }
                        break;
                    }

                    default:
                        foreach (var child in Compilation.BoundChildren(statement))
                        {
                            ScanNode(child, state);
                        }
                        break;
                }
            }

            private void ScanNode(BoundNode node, bool[] state)
            {
                if (node is BoundExpression expression)
                {
                    ScanExpression(expression, state);
                    return;
                }

                foreach (var child in Compilation.BoundChildren(node))
                {
                    ScanNode(child, state);
                }
            }

            public void ScanExpression(BoundExpression expression, bool[] state)
            {
                switch (expression.Kind)
                {
                    case BoundNodeKind.VariableExpression:
                    {
                        var variable = ((BoundVariableExpression)expression).Variable;
                        if (_diagnostics != null && IsTrackedAndUnassigned(variable, state, out var name))
                        {
                            _diagnostics.ReportUseOfUnassignedOutParameter(expression.Syntax.Location, name);
                        }
                        break;
                    }

                    case BoundNodeKind.AssignmentExpression:
                    {
                        var assignment = (BoundAssignmentExpression)expression;
                        ScanExpression(assignment.Expression, state);
                        MarkAssigned(assignment.Variable, state);
                        break;
                    }

                    case BoundNodeKind.CompoundAssignmentExpression:
                    {
                        var compound = (BoundCompoundAssignmentExpression)expression;
                        if (_diagnostics != null && IsTrackedAndUnassigned(compound.Variable, state, out var compoundName))
                        {
                            _diagnostics.ReportUseOfUnassignedOutParameter(compound.Syntax.Location, compoundName);
                        }
                        ScanExpression(compound.Expression, state);
                        MarkAssigned(compound.Variable, state);
                        break;
                    }

                    case BoundNodeKind.MemberAssignmentExpression:
                    {
                        var memberAssignment = (BoundMemberAssignmentExpression)expression;
                        ScanNode(memberAssignment.Target, state);
                        ScanExpression(memberAssignment.Expression, state);
                        break;
                    }

                    case BoundNodeKind.ElementAssignmentExpression:
                    {
                        var elementAssignment = (BoundElementAssignmentExpression)expression;
                        ScanNode(elementAssignment.Target, state);
                        ScanExpression(elementAssignment.Expression, state);
                        break;
                    }

                    case BoundNodeKind.ByRefArgument:
                    {
                        var wrapped = (BoundByRefArgument)expression;
                        if (wrapped.Expression is BoundVariableExpression variable)
                        {
                            if (wrapped.IsRef)
                            {
                                // ref 实参：读语义，要求已赋值
                                if (_diagnostics != null && IsTrackedAndUnassigned(variable.Variable, state, out var refName))
                                {
                                    _diagnostics.ReportRefArgumentNotAssigned(variable.Syntax.Location, refName);
                                }
                            }
                            else
                            {
                                // out 实参：调用后视为已赋值（被调方契约）
                                MarkAssigned(variable.Variable, state);
                            }
                        }
                        else
                        {
                            // 字段/元素目标：接收者与索引是读语义
                            ScanNode(wrapped.Expression, state);
                        }
                        break;
                    }

                    default:
                        foreach (var child in Compilation.BoundChildren(expression))
                        {
                            ScanNode(child, state);
                        }
                        break;
                }
            }

            private bool IsTrackedAndUnassigned(VariableSymbol variable, bool[] state, out string name)
            {
                if (_indexOf.TryGetValue(variable, out var index) && !state[index])
                {
                    name = variable.Name;
                    return true;
                }

                name = string.Empty;
                return false;
            }

            private void MarkAssigned(VariableSymbol variable, bool[] state)
            {
                if (_indexOf.TryGetValue(variable, out var index))
                {
                    state[index] = true;
                }
            }
        }
    }
}
