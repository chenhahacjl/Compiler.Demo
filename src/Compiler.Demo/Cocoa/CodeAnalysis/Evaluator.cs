using Cocoa.CodeAnalysis.Binding;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Cocoa.CodeAnalysis
{
    internal sealed class Evaluator
    {
        private readonly BoundStatement m_root;
        private readonly Dictionary<VariableSymbol, object> m_variables;

        private object m_lastValue;

        public Evaluator(BoundStatement root, Dictionary<VariableSymbol, object> variables)
        {
            m_root = root;
            m_variables = variables;
        }

        public object Evaluate()
        {
            EvaluateStatement(m_root);
            return m_lastValue;
        }

        private void EvaluateStatement(BoundStatement node)
        {
            switch (node.Kind)
            {
                case BoundNodeKind.BlockStatement:
                    EvaluateBlockStatement((BoundBlockStatement)node);
                    break;
                case BoundNodeKind.VariableDeclaration:
                    EvaluateVariableDeclaration((BoundVariableDeclaration)node);
                    break;
                case BoundNodeKind.IfStatement:
                    EvaluateIfStatement((BoundIfStatement)node);
                    break;
                case BoundNodeKind.WhileStatement:
                    EvaluateWhileStatement((BoundWhileStatement)node);
                    break;
                case BoundNodeKind.ForStatement:
                    EvaluateForStatement((BoundForStatement)node);
                    break;
                case BoundNodeKind.ExpressionStatement:
                    EvaluateExpressionStatement((BoundExpressionStatement)node);
                    break;
                default:
                    throw new Exception($"Unexcepted node {node.Kind}");
            }
        }

        private void EvaluateVariableDeclaration(BoundVariableDeclaration node)
        {
            var value = EvaluateExpression(node.Initializer);
            m_variables[node.Variable] = value;
            m_lastValue = value;
        }

        private void EvaluateBlockStatement(BoundBlockStatement node)
        {
            foreach (var statement in node.Statements)
            {
                EvaluateStatement(statement);
            }
        }

        private void EvaluateIfStatement(BoundIfStatement node)
        {
            var condition = (bool)EvaluateExpression(node.Condition);
            if (condition)
            {
                EvaluateStatement(node.ThenStatement);
            }
            else if (node.ElseStatement != null)
            {
                EvaluateStatement(node.ElseStatement);
            }
        }

        private void EvaluateWhileStatement(BoundWhileStatement node)
        {
            while ((bool)EvaluateExpression(node.Condition))
            {
                EvaluateStatement(node.Body);
            }
        }

        private void EvaluateForStatement(BoundForStatement node)
        {
            var lowerBound = (int)EvaluateExpression(node.LowerBound);
            var upperBound = (int)EvaluateExpression(node.UpperBound);

            for (int i = lowerBound; i <= upperBound; i++)
            {
                m_variables[node.Variable] = i;
                EvaluateStatement(node.Body);
            }
        }

        private void EvaluateExpressionStatement(BoundExpressionStatement node)
        {
            m_lastValue = EvaluateExpression(node.Expression);
        }

        private object EvaluateExpression(BoundExpression node)
        {
            switch (node.Kind)
            {
                case BoundNodeKind.LiteralExpression:
                    return EvaluateLiteralExpression((BoundLiteralExpression)node);
                case BoundNodeKind.VariableExpression:
                    return EvaluateVariableExpression((BoundVariableExpression)node);
                case BoundNodeKind.AssignmentExpression:
                    return EvaluateAssignmentExpression((BoundAssignmentExpression)node);
                case BoundNodeKind.UnaryExpression:
                    return EvaluateUnaryExpression((BoundUnaryExpression)node);
                case BoundNodeKind.BinaryExpression:
                    return EvaluateBinaryExpression((BoundBinaryExpression)node);
                default:
                    throw new Exception($"Unexcepted node {node.Kind}");
            }
        }

        private static object EvaluateLiteralExpression(BoundLiteralExpression literal)
        {
            return literal.Value;
        }

        private object EvaluateVariableExpression(BoundVariableExpression variable)
        {
            return m_variables[variable.Variable];
        }

        private object EvaluateAssignmentExpression(BoundAssignmentExpression assignment)
        {
            var value = EvaluateExpression(assignment.Expression);
            m_variables[assignment.Variable] = value;

            return value;
        }

        private object EvaluateUnaryExpression(BoundUnaryExpression unary)
        {
            var operand = EvaluateExpression(unary.Operand);

            switch (unary.Op.Kind)
            {
                case BoundUnaryOperatorKind.Identity: return (int)operand;
                case BoundUnaryOperatorKind.Negation: return -(int)operand;
                case BoundUnaryOperatorKind.LogicalNegation: return !(bool)operand;
                case BoundUnaryOperatorKind.OnesComplement: return ~(int)operand;
                default:
                    throw new Exception($"Unexcepted unary operator {unary.Op}");
            }
        }

        private object EvaluateBinaryExpression(BoundBinaryExpression binary)
        {
            var left = EvaluateExpression(binary.Left);
            var right = EvaluateExpression(binary.Right);

            switch (binary.Op.Kind)
            {
                case BoundBinaryOperatorKind.Addition: return (int)left + (int)right;
                case BoundBinaryOperatorKind.Subtraction: return (int)left - (int)right;
                case BoundBinaryOperatorKind.Multiplication: return (int)left * (int)right;
                case BoundBinaryOperatorKind.Division: return (int)left / (int)right;
                case BoundBinaryOperatorKind.BitwiseAnd: return binary.Type == typeof(int) ? (int)left & (int)right : (bool)left & (bool)right;
                case BoundBinaryOperatorKind.BitwiseOr: return binary.Type == typeof(int) ? (int)left | (int)right : (bool)left | (bool)right;
                case BoundBinaryOperatorKind.BitwiseXor: return binary.Type == typeof(int) ? (int)left ^ (int)right : (bool)left ^ (bool)right;
                case BoundBinaryOperatorKind.LogicalAnd: return (bool)left && (bool)right;
                case BoundBinaryOperatorKind.LogicalOr: return (bool)left || (bool)right;
                case BoundBinaryOperatorKind.Equals: return Equals(left, right);
                case BoundBinaryOperatorKind.NotEquals: return !Equals(left, right);
                case BoundBinaryOperatorKind.Less: return (int)left < (int)right;
                case BoundBinaryOperatorKind.LessOrEquals: return (int)left <= (int)right;
                case BoundBinaryOperatorKind.Greater: return (int)left > (int)right;
                case BoundBinaryOperatorKind.GreaterOrEquals: return (int)left >= (int)right;
                default:
                    throw new Exception($"Unexcepted binary operator {binary.Op}");
            }
        }
    }
}
