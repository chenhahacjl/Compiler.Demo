using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using System.Collections.Immutable;

using static Cocoa.CodeAnalysis.Binding.BoundNodeFactory;

namespace Cocoa.CodeAnalysis.Lowering
{
    /// <summary>
    /// 语法降级器
    /// </summary>
    public sealed class Lowerer : BoundTreeRewriter
    {
        private int _labelCount;

        private Lowerer()
        {
        }

        private BoundLabel GenerateLabel()
        {
            var name = $"Label{++_labelCount}";
            return new BoundLabel(name);
        }

        public static BoundBlockStatement Lower(FunctionSymbol function, BoundStatement statement)
        {
            var lowerer = new Lowerer();
            var result = lowerer.RewriteStatement(statement);

            return RemoveDeadCode(Flatten(function, result));
        }

        private static BoundBlockStatement Flatten(FunctionSymbol function, BoundStatement statement)
        {
            var builder = ImmutableArray.CreateBuilder<BoundStatement>();
            var stack = new Stack<BoundStatement>();

            stack.Push(statement);

            while (stack.Count > 0)
            {
                var current = stack.Pop();

                if (current is BoundBlockStatement block)
                {
                    foreach (var s in block.Statements.Reverse())
                    {
                        stack.Push(s);
                    }
                }
                else
                {
                    builder.Add(current);
                }
            }

            if (function.ReturnType == TypeSymbol.Void)
            {
                if (builder.Count == 0 || CanFallThrough(builder.Last()))
                {
                    builder.Add(new BoundReturnStatement(statement.Syntax, null));
                }
            }

            return new BoundBlockStatement(statement.Syntax, builder.ToImmutable());
        }

        private static bool CanFallThrough(BoundStatement boundStatement)
        {
            return boundStatement.Kind != BoundNodeKind.ReturnStatement &&
                   boundStatement.Kind != BoundNodeKind.GotoStatement &&
                   boundStatement.Kind != BoundNodeKind.ThrowStatement;
        }

        private static BoundBlockStatement RemoveDeadCode(BoundBlockStatement node)
        {
            var controlFlow = ControlFlowGraph.Create(node);
            var reachableStatements = new HashSet<BoundStatement>(
                controlFlow.Blocks.SelectMany(b => b.Statements));

            var builder = node.Statements.ToBuilder();
            for (var i = builder.Count - 1; i >= 0; i--)
            {
                if (!reachableStatements.Contains(builder[i]))
                {
                    builder.RemoveAt(i);
                }
            }

            return new BoundBlockStatement(node.Syntax, builder.ToImmutable());
        }

        protected override BoundStatement RewriteIfStatement(BoundIfStatement node)
        {
            if (node.ElseStatement == null)
            {
                // if <condition>
                //     <then>
                //
                // ---->
                //
                // gotoFalse <condition> end
                // <then>
                // end:

                var endLabel = GenerateLabel();

                var result = Block(
                    node.Syntax,
                    GotoFalse(node.Syntax, endLabel, node.Condition),
                    node.ThenStatement,
                    Label(node.Syntax, endLabel)
                );

                return RewriteStatement(result);
            }
            else
            {
                // if <condition>
                //     <then>
                // else
                //     <else>
                //
                // ---->
                //
                // gotoFalse <condition> else
                // <then>
                // goto end
                // else:
                // <else>
                // end:

                var elseLabel = GenerateLabel();
                var endLabel = GenerateLabel();

                var result = Block(
                    node.Syntax,
                    GotoFalse(node.Syntax, elseLabel, node.Condition),
                    node.ThenStatement,
                    Goto(node.Syntax, endLabel),
                    Label(node.Syntax, elseLabel),
                    node.ElseStatement,
                    Label(node.Syntax, endLabel)
                );

                return RewriteStatement(result);
            }
        }

        protected override BoundStatement RewriteWhileStatement(BoundWhileStatement node)
        {
            // while <condition>
            //     <body>
            //
            // ---->
            //
            // goto continue
            // body:
            // <body>
            // continue:
            // gotoTrue <condition> body
            // break:

            var bodyLabel = GenerateLabel();

            var result = Block(
                node.Syntax,
                Goto(node.Syntax, node.ContinueLabel),
                Label(node.Syntax, bodyLabel),
                node.Body,
                Label(node.Syntax, node.ContinueLabel),
                GotoTrue(node.Syntax, bodyLabel, node.Condition),
                Label(node.Syntax, node.BreakLabel)
            );

            return RewriteStatement(result);
        }

        protected override BoundStatement RewriteDoWhileStatement(BoundDoWhileStatement node)
        {
            // do
            //     <body>
            // while <condition>
            //
            // ----->
            //
            // body:
            // <body>
            // continue:
            // gotoTrue <condition> body
            // break:

            var bodyLabel = GenerateLabel();

            var result = Block(
                node.Syntax,
                Label(node.Syntax, bodyLabel),
                node.Body,
                Label(node.Syntax, node.ContinueLabel),
                GotoTrue(node.Syntax, bodyLabel, node.Condition),
                Label(node.Syntax, node.BreakLabel)
            );

            return RewriteStatement(result);
        }

        protected override BoundStatement RewriteForRangeStatement(BoundForRangeStatement node)
        {
            // for <var> = <lower> to <upper> [step <n>]
            //     <body>
            //
            // ---->
            //
            // {
            //     var <var> = <lower>
            //     let upperBound = <upper>
            //     while (<var> <= upperBound)              // 升序（step > 0 或缺省）
            //     while (<var> >= upperBound)              // 降序（step < 0）
            //     {
            //         <body>
            //         continue:
            //         <var> = <var> + <step>               // 降序时 step 为负即递减
            //     }
            // }

            var lowerBound = VariableDeclaration(node.Syntax, node.Variable, node.LowerBound);
            var upperBound = ConstantDeclaration(node.Syntax, "upperBound", node.UpperBound);

            // 步长方向：负数 → 降序（i >= upper 继续）；非负或缺省 → 升序（i <= upper 继续）
            var descending = node.Step?.ConstantValue?.Value is int stepValue && stepValue < 0;
            var condition = descending
                ? GreaterOrEqual(node.Syntax, Variable(node.Syntax, lowerBound), Variable(node.Syntax, upperBound))
                : LessOrEqual(node.Syntax, Variable(node.Syntax, lowerBound), Variable(node.Syntax, upperBound));

            // 步长：有 step 时 `i = i + step`，否则 `i = i + 1`
            BoundExpressionStatement increment;
            if (node.Step != null)
            {
                increment = new BoundExpressionStatement(
                    node.Syntax,
                    Assignment(
                        node.Syntax,
                        node.Variable,
                        Add(node.Syntax, Variable(node.Syntax, node.Variable), node.Step)));
            }
            else
            {
                increment = Increment(node.Syntax, Variable(node.Syntax, node.Variable));
            }

            var result = Block(
                node.Syntax,
                lowerBound,
                upperBound,
                While(node.Syntax,
                    condition,
                    Block(
                        node.Syntax,
                        node.Body,
                        Label(node.Syntax, node.ContinueLabel),
                        increment
                    ),
                    node.BreakLabel,
                    continueLabel: GenerateLabel()
                )
            );

            return RewriteStatement(result);
        }

        protected override BoundStatement RewriteConditionalGotoStatement(BoundConditionalGotoStatement node)
        {
            if (node.Condition.ConstantValue != null)
            {
                var condition = (bool)node.Condition.ConstantValue.Value;
                condition = node.JumpIfTrue ? condition : !condition;

                if (condition)
                {
                    return RewriteStatement(Goto(node.Syntax, node.Label));
                }
                else
                {
                    return RewriteStatement(Nop(node.Syntax));
                }
            }

            return base.RewriteConditionalGotoStatement(node);
        }

        protected override BoundExpression RewriteCompoundAssignmentExpression(BoundCompoundAssignmentExpression node)
        {
            var newNode = (BoundCompoundAssignmentExpression)base.RewriteCompoundAssignmentExpression(node);

            // a <op>= b
            //
            // ---->
            //
            // a = (a <op> b)

            var result = Assignment(
                newNode.Syntax,
                newNode.Variable,
                Binary(
                    newNode.Syntax,
                    Variable(newNode.Syntax, newNode.Variable),
                    newNode.Op,
                    newNode.Expression
                )
            );

            return result;
        }

        protected override BoundStatement RewriteVariableDeclaration(BoundVariableDeclaration node)
        {
            var rewrittenNode = base.RewriteVariableDeclaration(node);

            // S-7：反序列化库体（.coa raw HIR）节点 Syntax 为 null——SequencePoint 仅调试序列点定位用，
            // 库体无源码映射，跳过包装（语义不变；后端无需其存在）。
            return rewrittenNode.Syntax == null
                ? rewrittenNode
                : new BoundSequencePointStatement(rewrittenNode.Syntax, rewrittenNode, rewrittenNode.Syntax.Location);
        }

        protected override BoundStatement RewriteExpressionStatement(BoundExpressionStatement node)
        {
            var rewrittenNode = base.RewriteExpressionStatement(node);

            // S-7：同上——防反序列化库体 null Syntax 在取 .Location 时 NRE
            return rewrittenNode.Syntax == null
                ? rewrittenNode
                : new BoundSequencePointStatement(rewrittenNode.Syntax, rewrittenNode, rewrittenNode.Syntax.Location);
        }
    }
}
