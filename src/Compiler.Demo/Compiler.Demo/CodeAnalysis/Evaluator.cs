using System;
using System.Text;
using System.Threading.Tasks;

namespace Compiler.Demo.CodeAnalysis
{
    public class Evaluator
    {
        private readonly ExpressionSyntax m_root;

        public Evaluator(ExpressionSyntax root)
        {
            m_root = root;
        }

        public int Evaluate()
        {
            return EvaluateExpression(m_root);
        }

        private int EvaluateExpression(ExpressionSyntax m_node)
        {
            // BinaryExpression
            // NumberExpression

            if (m_node is NumberExpressionSyntax number)
            {
                return (int)number.NumberToken.Value;
            }

            if (m_node is BinaryExpressionSyntax binary)
            {
                var left = EvaluateExpression(binary.Left);
                var right = EvaluateExpression(binary.Right);

                if (binary.OperationToken.Kind == SyntaxKind.PlusToken)
                {
                    return left + right;
                }
                else if (binary.OperationToken.Kind == SyntaxKind.MinusToken)
                {
                    return left - right;
                }
                else if (binary.OperationToken.Kind == SyntaxKind.StarToken)
                {
                    return left * right;
                }
                else if (binary.OperationToken.Kind == SyntaxKind.SlashToken)
                {
                    return left / right;
                }
                else
                {
                    throw new Exception($"Unexcepted binary operator {binary.OperationToken.Kind}");
                }
            }

            if (m_node is ParenthesizedExpressionSyntax parenthesized)
            {
                return EvaluateExpression(parenthesized.Expression);
            }

            throw new Exception($"Unexcepted node {m_node.Kind}");
        }
    }
}
