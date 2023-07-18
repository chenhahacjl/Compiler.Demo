using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class ControlFlowGraph
    {
        public ControlFlowGraph(BasicBlock start, BasicBlock end, List<BasicBlock> blocks, List<BasicBlockBranch> branches)
        {
            Start = start;
            End = end;
            Blocks = blocks;
            Branches = branches;
        }

        public BasicBlock Start { get; }
        public BasicBlock End { get; }
        public List<BasicBlock> Blocks { get; }
        public List<BasicBlockBranch> Branches { get; }

        public sealed class BasicBlock
        {
            public BasicBlock()
            {

            }

            public BasicBlock(bool isStart)
            {
                IsStart = isStart;
                IsEnd = !isStart;
            }

            public bool IsStart { get; }
            public bool IsEnd { get; }

            public List<BoundStatement> Statements { get; } = new List<BoundStatement>();
            public List<BasicBlockBranch> Incoming { get; } = new List<BasicBlockBranch>();
            public List<BasicBlockBranch> Outgoing { get; } = new List<BasicBlockBranch>();

            public override string ToString()
            {
                if (IsStart)
                    return "<Start>";

                if (IsEnd)
                    return "<End>";

                using (var stringWrite = new StringWriter())
                {
                    foreach (var statement in Statements)
                        statement.WriteTo(stringWrite);

                    return stringWrite.ToString();
                }
            }
        }

        public sealed class BasicBlockBranch
        {
            public BasicBlockBranch(BasicBlock from, BasicBlock to, BoundExpression condition)
            {
                From = from;
                To = to;
                Condition = condition;
            }

            public BasicBlock From { get; }
            public BasicBlock To { get; }
            public BoundExpression Condition { get; }

            public override string ToString()
            {
                if (Condition == null)
                    return string.Empty;

                return Condition.ToString();
            }
        }

        public sealed class BasicBlockBuilder
        {
            private List<BoundStatement> m_statements = new List<BoundStatement>();
            private List<BasicBlock> m_blocks = new List<BasicBlock>();

            public List<BasicBlock> Build(BoundBlockStatement block)
            {
                foreach (var statement in block.Statements)
                {
                    switch (statement.Kind)
                    {
                        case BoundNodeKind.LabelStatement:
                            StartBlock();
                            m_statements.Add(statement);
                            break;
                        case BoundNodeKind.GotoStatement:
                        case BoundNodeKind.ConditionalGotoStatement:
                        case BoundNodeKind.ReturnStatement:
                            m_statements.Add(statement);
                            StartBlock();
                            break;
                        case BoundNodeKind.VariableDeclaration:
                        case BoundNodeKind.ExpressionStatement:
                            m_statements.Add(statement);
                            break;
                        default:
                            throw new Exception($"Unexpected statement: {statement.Kind}");
                    }
                }

                EndBlock();

                return m_blocks.ToList();
            }

            private void StartBlock()
            {
                EndBlock();
            }

            private void EndBlock()
            {
                if (m_statements.Count > 0)
                {
                    var block = new BasicBlock();
                    block.Statements.AddRange(m_statements);
                    m_blocks.Add(block);
                    m_statements.Clear();
                }
            }
        }

        public sealed class GraphBuilder
        {
            private Dictionary<BoundStatement, BasicBlock> m_blockFromStatement = new Dictionary<BoundStatement, BasicBlock>();
            private Dictionary<BoundLabel, BasicBlock> m_blockFromLabel = new Dictionary<BoundLabel, BasicBlock>();
            private List<BasicBlockBranch> m_branches = new List<BasicBlockBranch>();

            private BasicBlock m_start = new BasicBlock(true);
            private BasicBlock m_end = new BasicBlock(false);

            public ControlFlowGraph Build(List<BasicBlock> blocks)
            {
                if (!blocks.Any())
                    Connect(m_start, m_end);
                else
                    Connect(m_start, blocks.First());

                foreach (var block in blocks)
                {
                    foreach (var statement in block.Statements)
                    {
                        m_blockFromStatement.Add(statement, block);

                        if (statement is BoundLabelStatement labelStatement)
                            m_blockFromLabel.Add(labelStatement.Label, block);
                    }
                }

                for (int i = 0; i < blocks.Count; i++)
                {
                    var current = blocks[i];
                    var next = i == blocks.Count - 1 ? m_end : blocks[i + 1];

                    foreach (var statement in current.Statements)
                    {
                        var isLastStatementent = statement == current.Statements.Last();
                        switch (statement.Kind)
                        {
                            case BoundNodeKind.GotoStatement:
                                var gs = (BoundGotoStatement)statement;
                                var toBlock = m_blockFromLabel[gs.Label];
                                Connect(current, toBlock);
                                break;
                            case BoundNodeKind.ConditionalGotoStatement:
                                var cgs = (BoundConditionalGotoStatement)statement;
                                var thenBlock = m_blockFromLabel[cgs.Label];
                                var elseBlock = next;
                                var negatedCondition = Negate(cgs.Condition);
                                var thenCondition = cgs.JumpIfTrue ? cgs.Condition : negatedCondition;
                                var elseCondition = cgs.JumpIfTrue ? negatedCondition : cgs.Condition;
                                Connect(current, thenBlock, thenCondition);
                                Connect(current, elseBlock, elseCondition);
                                break;
                            case BoundNodeKind.ReturnStatement:
                                Connect(current, m_end);
                                break;
                            case BoundNodeKind.VariableDeclaration:
                            case BoundNodeKind.LabelStatement:
                            case BoundNodeKind.ExpressionStatement:
                                if (isLastStatementent)
                                    Connect(current, next);
                                break;
                            default:
                                throw new Exception($"Unexpected statement: {statement.Kind}");
                        }
                    }
                }

            ScanAgain:
                foreach (var block in blocks)
                {
                    if (!block.Incoming.Any())
                    {
                        RemoveBlock(blocks, block);
                        goto ScanAgain;
                    }
                }

                blocks.Insert(0, m_start);
                blocks.Add(m_end);

                return new ControlFlowGraph(m_start, m_end, blocks, m_branches);
            }

            private void Connect(BasicBlock from, BasicBlock to, BoundExpression condition = null)
            {
                if (condition is BoundLiteralExpression literalExpression)
                {
                    var value = (bool)literalExpression.Value;

                    if (value)
                        condition = null;
                    else
                        return;
                }

                var branch = new BasicBlockBranch(from, to, condition);

                from.Outgoing.Add(branch);
                to.Incoming.Add(branch);
                m_branches.Add(branch);
            }

            private void RemoveBlock(List<BasicBlock> blocks, BasicBlock block)
            {
                foreach (var branch in block.Incoming)
                {
                    branch.From.Outgoing.Remove(branch);
                    m_branches.Remove(branch);
                }

                foreach (var branch in block.Outgoing)
                {
                    branch.To.Incoming.Remove(branch);
                    m_branches.Remove(branch);
                }

                blocks.Remove(block);
            }

            private BoundExpression Negate(BoundExpression condition)
            {
                if (condition is BoundLiteralExpression literal)
                {
                    var value = (bool)literal.Value;

                    return new BoundLiteralExpression(!value);
                }

                var op = BoundUnaryOperator.Bind(Syntax.SyntaxKind.BangToken, Symbols.TypeSymbol.Boolean);
                return new BoundUnaryExpression(op, condition);
            }
        }

        public void WriteTo(TextWriter writer)
        {
            string Quote(string text)
            {
                return "\"" + text.Replace("\"", "\\\"") + "\"";
            }

            writer.WriteLine("digraph G {");

            var blockIds = new Dictionary<BasicBlock, string>();

            for (int i = 0; i < Blocks.Count; i++)
            {
                var id = $"N{i}";
                blockIds.Add(Blocks[i], id);
            }

            foreach (var block in Blocks)
            {
                var id = blockIds[block];
                var label = Quote(block.ToString().Replace(Environment.NewLine, "\\l"));

                writer.WriteLine($"    {id} [label = {label}, shape = box]");
            }

            foreach (var branch in Branches)
            {
                var fromId = blockIds[branch.From];
                var toId = blockIds[branch.To];
                var label = Quote(branch.ToString());

                writer.WriteLine($"    {fromId} -> {toId} [label = {label}]");
            }

            writer.WriteLine("}");
        }

        public static ControlFlowGraph Create(BoundBlockStatement body)
        {
            var basicBlockBuilder = new BasicBlockBuilder();
            var blocks = basicBlockBuilder.Build(body);

            var graphBuilder = new GraphBuilder();
            return graphBuilder.Build(blocks);
        }

        public static bool AllPathsReturn(BoundBlockStatement body)
        {
            var graph = Create(body);

            foreach (var branch in graph.End.Incoming)
            {
                var lastStatement = branch.From.Statements.Last();
                if (lastStatement.Kind != BoundNodeKind.ReturnStatement)
                    return false;
            }

            return true;
        }
    }
}
