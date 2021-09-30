using Cocoa.CodeAnalysis.Binding;
using System;
using System.Collections;
using System.Text;
using System.Threading.Tasks;

namespace Cocoa.CodeAnalysis
{
    internal sealed class Evaluator
    {
        private readonly BoundExpression m_root;

        public Evaluator(BoundExpression root)
        {
            m_root = root;
        }

        public object Evaluate()
        {
            return EvaluateExpression(m_root);
        }

        private object EvaluateExpression(BoundExpression node)
        {
            if (node is BoundLiteralExpression literal)
            {
                return literal.Value;
            }

            if (node is BoundUnaryExpression unary)
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

            if (node is BoundBinaryExpression binary)
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

            throw new Exception($"Unexcepted node {node.Kind}");
        }
    }
}
