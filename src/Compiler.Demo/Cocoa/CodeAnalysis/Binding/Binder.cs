using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class Binder
    {
        private readonly DiagnosticBag m_diagnostics = new DiagnosticBag();

        public DiagnosticBag Diagnostics => m_diagnostics;

        public BoundExpression BindExpression(ExpressionSyntax syntax)
        {
            switch (syntax.Kind)
            {
                case SyntaxKind.LiteralExpression: return BindLiteralExpression((LiteralExpressionSyntax)syntax);
                case SyntaxKind.UnaryExpression: return BindUnaryExpression((UnaryExpressionSyntax)syntax);
                case SyntaxKind.BinaryExpression: return BindBinaryExpression((BinaryExpressionSyntax)syntax);
                case SyntaxKind.ParenthesizedExpression: return BindExpression(((ParenthesizedExpressionSyntax)syntax).Expression);
                default:
                    throw new Exception($"Unexcepted syntax {syntax.Kind}");
            }
        }

        private BoundExpression BindLiteralExpression(LiteralExpressionSyntax syntax)
        {
            var value = syntax.Value ?? 0;

            return new BoundLiteralExpression(value);
        }

        private BoundExpression BindUnaryExpression(UnaryExpressionSyntax syntax)
        {
            var boundOperand = BindExpression(syntax.Operand);
            var boundOperator = BoundUnaryOperator.Bind(syntax.OperationToken.Kind, boundOperand.Type);

            if (boundOperator == null)
            {
                m_diagnostics.ReportUndefinedUnaryOperator(syntax.OperationToken.Span, syntax.OperationToken.Text, boundOperand.Type);
                return boundOperand;
            }

            return new BoundUnaryExpression(boundOperator, boundOperand);
        }

        private BoundExpression BindBinaryExpression(BinaryExpressionSyntax syntax)
        {
            var boundLeft = BindExpression(syntax.Left);
            var boundRight = BindExpression(syntax.Right);
            var boundOperator = BoundBinaryOperator.Bind(syntax.OperationToken.Kind, boundLeft.Type, boundRight.Type);

            if (boundOperator == null)
            {
                m_diagnostics.ReportUndefinedBinaryOperator(syntax.OperationToken.Span, syntax.OperationToken.Text, boundLeft.Type, boundRight.Type);
                return boundLeft;
            }

            return new BoundBinaryExpression(boundLeft, boundOperator, boundRight);
        }
    }
}
