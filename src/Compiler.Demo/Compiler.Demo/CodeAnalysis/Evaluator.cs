using Compiler.Demo.CodeAnalysis.Binding;
using Compiler.Demo.CodeAnalysis.Syntax;
using System;
using System.Text;
using System.Threading.Tasks;

namespace Compiler.Demo.CodeAnalysis
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
                var operand = (int)EvaluateExpression(unary.Operand);

                switch (unary.OperatorKind)
                {
                    case BoundUnaryOperatorKind.Identity: return operand;
                    case BoundUnaryOperatorKind.Negation: return -operand;
                    default:
                        throw new Exception($"Unexcepted unary operator {unary.OperatorKind}");
                }
            }

            if (node is BoundBinaryExpression binary)
            {
                var left = (int)EvaluateExpression(binary.Left);
                var right = (int)EvaluateExpression(binary.Right);

                switch (binary.OperatorKind)
                {
                    case BoundBinaryOperatorKind.Addition: return left + right;
                    case BoundBinaryOperatorKind.Subtraction: return left - right;
                    case BoundBinaryOperatorKind.Multiplication: return left * right;
                    case BoundBinaryOperatorKind.Division: return left / right;
                    default:
                        throw new Exception($"Unexcepted binary operator {binary.OperatorKind}");
                }
            }

            //if (node is BoundParenthesizedExpression parenthesized)
            //{
            //    return EvaluateExpression(parenthesized.Expression);
            //}

            throw new Exception($"Unexcepted node {node.Kind}");
        }
    }
}
