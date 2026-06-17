using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;

using static Cocoa.CodeAnalysis.Binding.BoundNodeFactory;

namespace Cocoa.CodeAnalysis.Lowering
{
    /// <summary>
    /// 语法降级器
    /// </summary>
    internal sealed class Lowerer : BoundTreeRewriter
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

        private sealed class BreakLabelRewriter : BoundTreeRewriter
        {
            private readonly BoundLabel _oldBreak;
            private readonly BoundLabel _newBreak;

            public BreakLabelRewriter(BoundLabel oldBreak, BoundLabel newBreak)
            {
                _oldBreak = oldBreak;
                _newBreak = newBreak;
            }

            protected override BoundStatement RewriteGotoStatement(BoundGotoStatement node)
            {
                return node.Label == _oldBreak ? new BoundGotoStatement(node.Syntax, _newBreak) : node;
            }
        }

        public static BoundBlockStatement Lower(FunctionSymbol function, BoundStatement statement)
        {
            var lowerer = new Lowerer();
            var result = lowerer.RewriteStatement(statement);

            // Temporarily skip RemoveDeadCode to prevent CFG from incorrectly pruning switch labels
            return Flatten(function, result);
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
                   boundStatement.Kind != BoundNodeKind.GotoStatement;
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

        protected override BoundStatement RewriteSwitchStatement(BoundSwitchStatement node)
        {
            var endLabel = GenerateLabel();
            var builder = ImmutableArray.CreateBuilder<BoundStatement>();
            var breakRewriter = new BreakLabelRewriter(node.BreakLabel, endLabel);

            var defaultCase = node.Cases.FirstOrDefault(c => c.Value == null);
            var nonDefaults = node.Cases.Where(c => c.Value != null).ToList();

            // Create a temp variable to hold the switch expression
            var tempVar = new LocalVariableSymbol("__switchTemp", isReadOnly: true, node.Expression.Type, null);
            builder.Add(VariableDeclaration(node.Syntax, tempVar, node.Expression));
            var tempExpr = Variable(node.Syntax, tempVar);

            var caseCheckLabels = nonDefaults.Select(_ => GenerateLabel()).ToArray();
            var caseBodyLabels = nonDefaults.Select(_ => GenerateLabel()).ToArray();

            BoundLabel defaultCheckLabel = null!;
            var hasDefault = defaultCase != null;
            if (hasDefault)
                defaultCheckLabel = GenerateLabel();

            for (var i = 0; i < nonDefaults.Count; i++)
            {
                var caseNode = nonDefaults[i];
                var checkLabel = caseCheckLabels[i];
                var bodyLabel = caseBodyLabels[i];

                // Determine where to jump if the current case doesn't match
                var nextCheck = i < nonDefaults.Count - 1
                    ? caseCheckLabels[i + 1]
                    : (hasDefault ? defaultCheckLabel : endLabel);

                // Check label
                builder.Add(Label(node.Syntax, checkLabel));

                // If false, jump to next check (or default/end)
                builder.Add(GotoFalse(node.Syntax, nextCheck,
                    Binary(node.Syntax, tempExpr, SyntaxKind.EqualsEqualsToken, caseNode.Value!)));

                // Body label
                builder.Add(Label(node.Syntax, bodyLabel));

                // Use rewriter to map break to endLabel, then rewrite
                var rewrittenBody = breakRewriter.RewriteStatement(caseNode.Body);
                builder.Add(rewrittenBody);
                builder.Add(Goto(node.Syntax, endLabel));
            }

            // Default case
            if (hasDefault)
            {
                builder.Add(Label(node.Syntax, defaultCheckLabel));
                builder.Add(breakRewriter.RewriteStatement(defaultCase!.Body));
            }

            builder.Add(Label(node.Syntax, endLabel));

            var result = new BoundBlockStatement(node.Syntax, builder.ToImmutable());
            return RewriteStatement(result);
        }

        protected override BoundStatement RewriteForeachStatement(BoundForeachStatement node)
        {
            // foreach (x in collection) { body }
            // Simplified lowering: for now map to a simple pattern
            var bodyLabel = GenerateLabel();

            var result = Block(
                node.Syntax,
                Goto(node.Syntax, node.ContinueLabel),
                Label(node.Syntax, bodyLabel),
                node.Body,
                Label(node.Syntax, node.ContinueLabel),
                Goto(node.Syntax, node.BreakLabel),
                Label(node.Syntax, node.BreakLabel)
            );

            return RewriteStatement(result);
        }

        protected override BoundStatement RewriteForStatement(BoundForStatement node)
        {
            // for <var> = <lower> to <upper>
            //     <body>
            //
            // ---->
            //
            // {
            //     var <var> = <lower>
            //     let upperBound = <upper>
            //     while (<var> <= upperBound)
            //     {
            //         <body>
            //         continue:
            //         <var> = <var> + 1
            //     }
            // }

            var lowerBound = VariableDeclaration(node.Syntax, node.Variable, node.LowerBound);
            var upperBound = ConstantDeclaration(node.Syntax, "upperBound", node.UpperBound);

            var result = Block(
                node.Syntax,
                lowerBound,
                upperBound,
                While(node.Syntax,
                    LessOrEqual(
                        node.Syntax,
                        Variable(node.Syntax, lowerBound),
                        Variable(node.Syntax, upperBound)
                    ),
                    Block(
                        node.Syntax,
                        node.Body,
                        Label(node.Syntax, node.ContinueLabel),
                        Increment(
                            node.Syntax,
                            Variable(node.Syntax, lowerBound)
                    )
                ),
                node.BreakLabel,
                continueLabel: GenerateLabel())
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

            return new BoundSequencePointStatement(rewrittenNode.Syntax, rewrittenNode, rewrittenNode.Syntax.Location);
        }

        protected override BoundStatement RewriteExpressionStatement(BoundExpressionStatement node)
        {
            var rewrittenNode = base.RewriteExpressionStatement(node);

            return new BoundSequencePointStatement(rewrittenNode.Syntax, rewrittenNode, rewrittenNode.Syntax.Location);
        }
    }
}
