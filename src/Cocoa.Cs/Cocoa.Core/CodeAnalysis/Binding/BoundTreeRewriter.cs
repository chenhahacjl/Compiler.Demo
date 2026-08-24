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
            var step = node.Step == null ? null : RewriteExpression(node.Step);
            var body = RewriteStatement(node.Body);
            if (lowerBound == node.LowerBound && upperBound == node.UpperBound && step == node.Step && body == node.Body)
            {
                return node;
            }

            return new BoundForStatement(node.Syntax, node.Variable, lowerBound, upperBound, step, body, node.BreakLabel, node.ContinueLabel);
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
                case BoundNodeKind.FunctionValueExpression:
                {
                    return RewriteFunctionValueExpression((BoundFunctionValueExpression)node);
                }
                case BoundNodeKind.InvocationExpression:
                {
                    return RewriteInvocationExpression((BoundInvocationExpression)node);
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
                case BoundNodeKind.ConditionalExpression:
                {
                    return RewriteConditionalExpression((BoundConditionalExpression)node);
                }
                case BoundNodeKind.CallExpression:
                {
                    return RewriteCallExpression((BoundCallExpression)node);
                }
                case BoundNodeKind.ConversionExpression:
                {
                    return RewriteConversionExpression((BoundConversionExpression)node);
                }
                case BoundNodeKind.ArrayCreationExpression:
                {
                    return RewriteArrayCreationExpression((BoundArrayCreationExpression)node);
                }
                case BoundNodeKind.ElementAccessExpression:
                {
                    return RewriteElementAccessExpression((BoundElementAccessExpression)node);
                }
                case BoundNodeKind.ElementAssignmentExpression:
                {
                    return RewriteElementAssignmentExpression((BoundElementAssignmentExpression)node);
                }
                case BoundNodeKind.MemberAccessExpression:
                {
                    return RewriteMemberAccessExpression((BoundMemberAccessExpression)node);
                }
                case BoundNodeKind.MemberCallExpression:
                {
                    return RewriteMemberCallExpression((BoundMemberCallExpression)node);
                }
                case BoundNodeKind.MemberAssignmentExpression:
                {
                    return RewriteMemberAssignmentExpression((BoundMemberAssignmentExpression)node);
                }
                case BoundNodeKind.ObjectCreationExpression:
                {
                    return RewriteObjectCreationExpression((BoundObjectCreationExpression)node);
                }
                case BoundNodeKind.ThisExpression:
                {
                    return RewriteThisExpression((BoundThisExpression)node);
                }
                case BoundNodeKind.BaseExpression:
                {
                    return RewriteBaseExpression((BoundBaseExpression)node);
                }
                case BoundNodeKind.StaticTypeExpression:
                {
                    return RewriteStaticTypeExpression((BoundStaticTypeExpression)node);
                }
                case BoundNodeKind.ConstructorChainExpression:
                {
                    return RewriteConstructorChainExpression((BoundConstructorChainExpression)node);
                }
                case BoundNodeKind.FormatExpression:
                {
                    return RewriteFormatExpression((BoundFormatExpression)node);
                }
                case BoundNodeKind.IsExpression:
                {
                    return RewriteIsExpression((BoundIsExpression)node);
                }
                case BoundNodeKind.AsExpression:
                {
                    return RewriteAsExpression((BoundAsExpression)node);
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

        /// <summary>函数值（6e-M22 C4）：仅重写接收者；lambda 体不在表达式子树内（随符号入 Functions 清单）。</summary>
        protected virtual BoundExpression RewriteFunctionValueExpression(BoundFunctionValueExpression node)
        {
            if (node.Receiver == null)
            {
                return node;
            }

            var receiver = RewriteExpression(node.Receiver);
            return receiver == node.Receiver
                ? node
                : new BoundFunctionValueExpression(node.Syntax, node.Function, receiver, node.Body, (Symbols.FunctionTypeSymbol)node.Type);
        }

        protected virtual BoundExpression RewriteInvocationExpression(BoundInvocationExpression node)
        {
            var callee = RewriteExpression(node.Callee);

            ImmutableArray<BoundExpression>.Builder? builder = null;
            for (var i = 0; i < node.Arguments.Length; i++)
            {
                var oldArgument = node.Arguments[i];
                var newArgument = RewriteExpression(oldArgument);
                if (newArgument != oldArgument && builder == null)
                {
                    builder = ImmutableArray.CreateBuilder<BoundExpression>(node.Arguments.Length);
                    for (var j = 0; j < i; j++)
                    {
                        builder.Add(node.Arguments[j]);
                    }
                }

                builder?.Add(newArgument);
            }

            return callee == node.Callee && builder == null
                ? node
                : new BoundInvocationExpression(node.Syntax, callee, builder?.ToImmutable() ?? node.Arguments, node.Type);
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

        protected virtual BoundExpression RewriteConditionalExpression(BoundConditionalExpression node)
        {
            var condition = RewriteExpression(node.Condition);
            var whenTrue = RewriteExpression(node.WhenTrue);
            var whenFalse = RewriteExpression(node.WhenFalse);
            if (condition == node.Condition && whenTrue == node.WhenTrue && whenFalse == node.WhenFalse)
            {
                return node;
            }

            return new BoundConditionalExpression(node.Syntax, condition, whenTrue, whenFalse);
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

        protected virtual BoundExpression RewriteArrayCreationExpression(BoundArrayCreationExpression node)
        {
            var length = RewriteExpression(node.Length);
            var initializers = RewriteExpressions(node.Initializers);

            if (length == node.Length && initializers == node.Initializers)
            {
                return node;
            }

            return new BoundArrayCreationExpression(node.Syntax, node.Type, length, initializers);
        }

        protected virtual BoundExpression RewriteElementAccessExpression(BoundElementAccessExpression node)
        {
            var target = RewriteExpression(node.Target);
            var index = RewriteExpression(node.Index);

            if (target == node.Target && index == node.Index)
            {
                return node;
            }

            return new BoundElementAccessExpression(node.Syntax, node.Type, target, index);
        }

        protected virtual BoundExpression RewriteElementAssignmentExpression(BoundElementAssignmentExpression node)
        {
            var target = (BoundElementAccessExpression)RewriteExpression(node.Target);
            var expression = RewriteExpression(node.Expression);

            if (target == node.Target && expression == node.Expression)
            {
                return node;
            }

            return new BoundElementAssignmentExpression(node.Syntax, node.Type, target, expression);
        }

        protected virtual BoundExpression RewriteMemberAccessExpression(BoundMemberAccessExpression node)
        {
            var target = RewriteExpression(node.Target);
            if (target == node.Target)
            {
                return node;
            }

            return new BoundMemberAccessExpression(node.Syntax, node.Type, target, node.Identifier, node.Field);
        }

        protected virtual BoundExpression RewriteMemberCallExpression(BoundMemberCallExpression node)
        {
            var expression = RewriteExpression(node.Expression);
            var arguments = RewriteExpressions(node.Arguments);
            if (expression == node.Expression && arguments == node.Arguments)
            {
                return node;
            }

            return new BoundMemberCallExpression(node.Syntax, expression, node.Identifier, arguments, node.Type, node.Method, node.IsBase);
        }

        protected virtual BoundExpression RewriteMemberAssignmentExpression(BoundMemberAssignmentExpression node)
        {
            var target = RewriteExpression(node.Target);
            var expression = RewriteExpression(node.Expression);
            if (target == node.Target && expression == node.Expression)
            {
                return node;
            }

            return new BoundMemberAssignmentExpression(node.Syntax, target, node.Field, expression);
        }

        protected virtual BoundExpression RewriteObjectCreationExpression(BoundObjectCreationExpression node)
        {
            var arguments = RewriteExpressions(node.Arguments);
            if (arguments == node.Arguments)
            {
                return node;
            }

            return new BoundObjectCreationExpression(node.Syntax, (Symbols.ClassTypeSymbol)node.Type, arguments);
        }

        protected virtual BoundExpression RewriteThisExpression(BoundThisExpression node)
        {
            return node;
        }

        protected virtual BoundExpression RewriteBaseExpression(BoundBaseExpression node)
        {
            return node;
        }

        protected virtual BoundExpression RewriteStaticTypeExpression(BoundStaticTypeExpression node)
        {
            return node;
        }

        protected virtual BoundExpression RewriteConstructorChainExpression(BoundConstructorChainExpression node)
        {
            var arguments = RewriteExpressions(node.Arguments);
            if (arguments == node.Arguments)
            {
                return node;
            }

            return new BoundConstructorChainExpression(node.Syntax, node.InitializerKind, node.Constructor, arguments);
        }

        protected virtual BoundExpression RewriteFormatExpression(BoundFormatExpression node)
        {
            var value = RewriteExpression(node.Value);
            if (value == node.Value)
            {
                return node;
            }

            return new BoundFormatExpression(node.Syntax, value, node.Width, node.Format);
        }

        protected virtual BoundExpression RewriteIsExpression(BoundIsExpression node)
        {
            var expression = RewriteExpression(node.Expression);
            if (expression == node.Expression)
            {
                return node;
            }

            return new BoundIsExpression(node.Syntax, expression, node.TargetType);
        }

        protected virtual BoundExpression RewriteAsExpression(BoundAsExpression node)
        {
            var expression = RewriteExpression(node.Expression);
            if (expression == node.Expression)
            {
                return node;
            }

            return new BoundAsExpression(node.Syntax, expression, node.TargetType);
        }

        private ImmutableArray<BoundExpression> RewriteExpressions(ImmutableArray<BoundExpression> expressions)
        {
            ImmutableArray<BoundExpression>.Builder? builder = null;

            for (var i = 0; i < expressions.Length; i++)
            {
                var oldExpression = expressions[i];
                var newExpression = RewriteExpression(oldExpression);
                if (newExpression != oldExpression)
                {
                    if (builder == null)
                    {
                        builder = ImmutableArray.CreateBuilder<BoundExpression>(expressions.Length);

                        for (var j = 0; j < i; j++)
                        {
                            builder.Add(expressions[j]);
                        }
                    }
                }

                if (builder != null)
                {
                    builder.Add(newExpression);
                }
            }

            return builder == null ? expressions : builder.MoveToImmutable();
        }
    }
}
