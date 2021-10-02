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
        private readonly BoundExpression m_root;
        private readonly Dictionary<VariableSymbol, object> m_variables;

        public Evaluator(BoundExpression root, Dictionary<VariableSymbol, object> variables)
        {
            m_root = root;
            m_variables = variables;
        }

        public object Evaluate()
        {
            return EvaluateExpression(m_root);
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
                case BoundBinaryOperatorKind.LogicalAnd: return (bool)left && (bool)right;
                case BoundBinaryOperatorKind.LogicanOr: return (bool)left || (bool)right;
                case BoundBinaryOperatorKind.Equals: return Equals(left, right);
                case BoundBinaryOperatorKind.NotEquals: return !Equals(left, right);
                default:
                    throw new Exception($"Unexcepted binary operator {binary.Op}");
            }
        }
    }
}
