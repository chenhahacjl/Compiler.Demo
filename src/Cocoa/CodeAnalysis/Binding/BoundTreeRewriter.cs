using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定树重写器
    /// </summary>
    internal abstract class BoundTreeRewriter
    {
        public virtual BoundStatement RewriteStatement(BoundStatement node)
        {
            switch (node.Kind)
            {
                case BoundNodeKind.BlockStatement:
                    return RewriteBlockStatement((BoundBlockStatement)node);
                case BoundNodeKind.NopStatement:
                    return RewriteNopStatement((BoundNopStatement)node);
                case BoundNodeKind.VariableDeclaration:
                    return RewriteVariableDeclaration((BoundVariableDeclaration)node);
                case BoundNodeKind.IfStatement:
                    return RewriteIfStatement((BoundIfStatement)node);
                case BoundNodeKind.WhileStatement:
                    return RewriteWhileStatement((BoundWhileStatement)node);
                case BoundNodeKind.DoWhileStatement:
                    return RewriteDoWhileStatement((BoundDoWhileStatement)node);
                case BoundNodeKind.ForStatement:
                    return RewriteForStatement((BoundForStatement)node);
                case BoundNodeKind.SwitchStatement:
                    return RewriteSwitchStatement((BoundSwitchStatement)node);
                case BoundNodeKind.ForeachStatement:
                    return RewriteForeachStatement((BoundForeachStatement)node);
                case BoundNodeKind.LabelStatement:
                    return RewriteLabelStatement((BoundLabelStatement)node);
                case BoundNodeKind.GotoStatement:
                    return RewriteGotoStatement((BoundGotoStatement)node);
                case BoundNodeKind.ConditionalGotoStatement:
                    return RewriteConditionalGotoStatement((BoundConditionalGotoStatement)node);
                case BoundNodeKind.ReturnStatement:
                    return RewriteReturnStatement((BoundReturnStatement)node);
                case BoundNodeKind.ExpressionStatement:
                    return RewriteExpressionStatement((BoundExpressionStatement)node);
                case BoundNodeKind.SequencePointStatement:
                    return RewriteSequencePointStatement((BoundSequencePointStatement)node);
                default:
                {
                    throw new Exception($"Unexpected node: {node.Kind}");
                }
            }
        }

        protected virtual BoundStatement RewriteBlockStatement(BoundBlockStatement node)
        {
            ImmutableArray<BoundStatement>.Builder? builder = null;

            for (var i = 0; i < node.Statements.Length; i++)
            {
                var oldStatement = node.Statements[i];
                var newStatement = RewriteStatement(oldStatement);
                if (newStatement != oldStatement)
                {
                    if (builder == null)
                    {
                        builder = ImmutableArray.CreateBuilder<BoundStatement>(node.Statements.Length);

                        for (var j = 0; j < i; j++)
                        {
                            builder.Add(node.Statements[j]);
                        }
                    }
                }

                if (builder != null)
                {
                    builder.Add(newStatement);
                }
            }

            if (builder == null)
            {
                return node;
            }

            return new BoundBlockStatement(node.Syntax, builder.MoveToImmutable());
        }

        protected virtual BoundStatement RewriteNopStatement(BoundNopStatement node)
        {
            return node;
        }

        protected virtual BoundStatement RewriteVariableDeclaration(BoundVariableDeclaration node)
        {
            var initializer = RewriteExpression(node.Initializer);
            if (initializer == node.Initializer)
            {
                return node;
            }

            return new BoundVariableDeclaration(node.Syntax, node.Variable, initializer);
        }

        protected virtual BoundStatement RewriteIfStatement(BoundIfStatement node)
        {
            var condition = RewriteExpression(node.Condition);
            var thenStatement = RewriteStatement(node.ThenStatement);
            var elseStatement = node.ElseStatement == null ? null : RewriteStatement(node.ElseStatement);
            if (condition == node.Condition && thenStatement == node.ThenStatement && elseStatement == node.ElseStatement)
            {
                return node;
            }

            return new BoundIfStatement(node.Syntax, condition, thenStatement, elseStatement);
        }

        protected virtual BoundStatement RewriteWhileStatement(BoundWhileStatement node)
        {
            var condition = RewriteExpression(node.Condition);
            var body = RewriteStatement(node.Body);
            if (condition == node.Condition && body == node.Body)
            {
                return node;
            }

            return new BoundWhileStatement(node.Syntax, condition, body, node.BreakLabel, node.ContinueLabel);
        }

        protected virtual BoundStatement RewriteDoWhileStatement(BoundDoWhileStatement node)
        {
            var body = RewriteStatement(node.Body);
            var condition = RewriteExpression(node.Condition);
            if (body == node.Body && condition == node.Condition)
            {
                return node;
            }

            return new BoundDoWhileStatement(node.Syntax, body, condition, node.BreakLabel, node.ContinueLabel);
        }

        protected virtual BoundStatement RewriteForStatement(BoundForStatement node)
        {
            var lowerBound = RewriteExpression(node.LowerBound);
            var upperBound = RewriteExpression(node.UpperBound);
            var body = RewriteStatement(node.Body);
            if (lowerBound == node.LowerBound && upperBound == node.UpperBound && body == node.Body)
            {
                return node;
            }

            return new BoundForStatement(node.Syntax, node.Variable, lowerBound, upperBound, body, node.BreakLabel, node.ContinueLabel);
        }

        protected virtual BoundStatement RewriteSwitchStatement(BoundSwitchStatement node)
        {
            var expression = RewriteExpression(node.Expression);
            ImmutableArray<BoundSwitchCase>.Builder? caseBuilder = null;

            for (var i = 0; i < node.Cases.Length; i++)
            {
                var caseNode = node.Cases[i];
                var caseValue = caseNode.Value == null ? null : RewriteExpression(caseNode.Value);
                var caseBody = RewriteStatement(caseNode.Body);

                if (caseValue != caseNode.Value || caseBody != caseNode.Body)
                {
                    caseBuilder ??= ImmutableArray.CreateBuilder<BoundSwitchCase>(node.Cases.Length);
                    for (var j = 0; j < i; j++)
                        caseBuilder.Add(node.Cases[j]);

                    caseBuilder.Add(new BoundSwitchCase(caseNode.Syntax, caseValue, caseBody));
                }
            }

            var newCases = caseBuilder == null ? node.Cases : caseBuilder.MoveToImmutable();

            if (expression == node.Expression && caseBuilder == null)
                return node;

            return new BoundSwitchStatement(node.Syntax, expression, newCases, node.BreakLabel);
        }

        protected virtual BoundStatement RewriteForeachStatement(BoundForeachStatement node)
        {
            var expression = RewriteExpression(node.Expression);
            var body = RewriteStatement(node.Body);
            if (expression == node.Expression && body == node.Body)
            {
                return node;
            }

            return new BoundForeachStatement(node.Syntax, node.Variable, expression, body, node.BreakLabel, node.ContinueLabel);
        }

        protected virtual BoundStatement RewriteLabelStatement(BoundLabelStatement node)
        {
            return node;
        }

        protected virtual BoundStatement RewriteGotoStatement(BoundGotoStatement node)
        {
            return node;
        }

        protected virtual BoundStatement RewriteConditionalGotoStatement(BoundConditionalGotoStatement node)
        {
            var confition = RewriteExpression(node.Condition);
            if (confition == node.Condition)
            {
                return node;
            }

            return new BoundConditionalGotoStatement(node.Syntax, node.Label, confition, node.JumpIfTrue);
        }

        private BoundStatement RewriteReturnStatement(BoundReturnStatement node)
        {
            var expression = node.Expression == null ? null : RewriteExpression(node.Expression);
            if (expression == node.Expression)
            {
                return node;
            }

            return new BoundReturnStatement(node.Syntax, expression);
        }

        protected virtual BoundStatement RewriteExpressionStatement(BoundExpressionStatement node)
        {
            var expression = RewriteExpression(node.Expression);
            if (expression == node.Expression)
            {
                return node;
            }

            return new BoundExpressionStatement(node.Syntax, expression);
        }

        protected virtual BoundStatement RewriteSequencePointStatement(BoundSequencePointStatement node)
        {
            var statement = RewriteStatement(node.Statement);
            if (statement == node.Statement)
            {
                return node;
            }

            return new BoundSequencePointStatement(node.Syntax, statement, node.Location);
        }

        public virtual BoundExpression RewriteExpression(BoundExpression node)
        {
            switch (node.Kind)
            {
                case BoundNodeKind.ErrorExpression:
                {
                    return RewriteErrorExpression((BoundErrorExpression)node);
                }
                case BoundNodeKind.LiteralExpression:
                {
                    return RewriteLiteralExpression((BoundLiteralExpression)node);
                }
                case BoundNodeKind.VariableExpression:
                {
                    return RewriteVariableExpression((BoundVariableExpression)node);
                }
                case BoundNodeKind.AssignmentExpression:
                {
                    return RewriteAssignmentExpression((BoundAssignmentExpression)node);
                }
                case BoundNodeKind.CompoundAssignmentExpression:
                {
                    return RewriteCompoundAssignmentExpression((BoundCompoundAssignmentExpression)node);
                }
                case BoundNodeKind.UnaryExpression:
                {
                    return RewriteUnaryExpression((BoundUnaryExpression)node);
                }
                case BoundNodeKind.BinaryExpression:
                {
                    return RewriteBinaryExpression((BoundBinaryExpression)node);
                }
                case BoundNodeKind.CallExpression:
                {
                    return RewriteCallExpression((BoundCallExpression)node);
                }
                case BoundNodeKind.ConversionExpression:
                {
                    return RewriteConversionExpression((BoundConversionExpression)node);
                }
                case BoundNodeKind.TernaryExpression:
                {
                    return RewriteTernaryExpression((BoundTernaryExpression)node);
                }
                case BoundNodeKind.PostfixUnaryExpression:
                {
                    return RewritePostfixUnaryExpression((BoundPostfixUnaryExpression)node);
                }
                default:
                {
                    throw new Exception($"Unexpected node: {node.Kind}");
                }
            }
        }

        protected virtual BoundExpression RewriteErrorExpression(BoundErrorExpression node)
        {
            return node;
        }

        protected virtual BoundExpression RewriteLiteralExpression(BoundLiteralExpression node)
        {
            return node;
        }

        protected virtual BoundExpression RewriteVariableExpression(BoundVariableExpression node)
        {
            return node;
        }

        protected virtual BoundExpression RewriteAssignmentExpression(BoundAssignmentExpression node)
        {
            var expression = RewriteExpression(node.Expression);
            if (expression == node.Expression)
            {
                return node;
            }

            return new BoundAssignmentExpression(node.Syntax, node.Variable, expression);
        }

        protected virtual BoundExpression RewriteCompoundAssignmentExpression(BoundCompoundAssignmentExpression node)
        {
            var expression = RewriteExpression(node.Expression);
            if (expression == node.Expression)
            {
                return node;
            }

            return new BoundCompoundAssignmentExpression(node.Syntax, node.Variable, node.Op, expression);
        }

        protected virtual BoundExpression RewriteUnaryExpression(BoundUnaryExpression node)
        {
            var operand = RewriteExpression(node.Operand);
            if (operand == node.Operand)
            {
                return node;
            }

            return new BoundUnaryExpression(node.Syntax, node.Op, operand);
        }

        protected virtual BoundExpression RewriteBinaryExpression(BoundBinaryExpression node)
        {
            var left = RewriteExpression(node.Left);
            var right = RewriteExpression(node.Right);
            if (left == node.Left && right == node.Right)
            {
                return node;
            }

            return new BoundBinaryExpression(node.Syntax, left, node.Op, right);
        }

        protected virtual BoundExpression RewriteCallExpression(BoundCallExpression node)
        {
            ImmutableArray<BoundExpression>.Builder? builder = null;

            for (var i = 0; i < node.Arguments.Length; i++)
            {
                var oldArgument = node.Arguments[i];
                var newArgument = RewriteExpression(oldArgument);
                if (newArgument != oldArgument)
                {
                    if (builder == null)
                    {
                        builder = ImmutableArray.CreateBuilder<BoundExpression>(node.Arguments.Length);

                        for (var j = 0; j < i; j++)
                        {
                            builder.Add(node.Arguments[j]);
                        }
                    }
                }

                if (builder != null)
                {
                    builder.Add(newArgument);
                }
            }

            if (builder == null)
            {
                return node;
            }

            return new BoundCallExpression(node.Syntax, node.Function, builder.MoveToImmutable());
        }

        protected virtual BoundExpression RewriteConversionExpression(BoundConversionExpression node)
        {
            var expression = RewriteExpression(node.Expression);
            if (expression == node.Expression)
            {
                return node;
            }

            return new BoundConversionExpression(node.Syntax, node.Type, expression);
        }

        protected virtual BoundExpression RewriteTernaryExpression(BoundTernaryExpression node)
        {
            var condition = RewriteExpression(node.Condition);
            var whenTrue = RewriteExpression(node.ThenExpression);
            var whenFalse = RewriteExpression(node.ElseExpression);
            if (condition == node.Condition && whenTrue == node.ThenExpression && whenFalse == node.ElseExpression)
            {
                return node;
            }

            return new BoundTernaryExpression(node.Syntax, condition, whenTrue, whenFalse);
        }

        protected virtual BoundExpression RewritePostfixUnaryExpression(BoundPostfixUnaryExpression node)
        {
            var operand = RewriteExpression(node.Operand);
            if (operand == node.Operand)
            {
                return node;
            }

            return new BoundPostfixUnaryExpression(node.Syntax, node.Op, operand);
        }
    }
}
