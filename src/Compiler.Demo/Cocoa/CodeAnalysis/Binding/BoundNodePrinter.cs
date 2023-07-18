using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.IO;
using System;
using System.CodeDom.Compiler;
using System.IO;

namespace Cocoa.CodeAnalysis.Binding
{
    internal static class BoundNodePrinter
    {
        public static void WriteTo(this BoundNode node, TextWriter writer)
        {
            if (writer is IndentedTextWriter indentedTextWriter)
            {
                WriteTo(node, indentedTextWriter);
            }
            else
            {
                WriteTo(node, new IndentedTextWriter(writer));
            }
        }

        public static void WriteTo(this BoundNode node, IndentedTextWriter writer)
        {
            switch (node.Kind)
            {
                case BoundNodeKind.BlockStatement:
                    WriteBlockStatement((BoundBlockStatement)node, writer);
                    break;
                case BoundNodeKind.VariableDeclaration:
                    WriteVariableDeclaration((BoundVariableDeclaration)node, writer);
                    break;
                case BoundNodeKind.IfStatement:
                    WriteIfStatement((BoundIfStatement)node, writer);
                    break;
                case BoundNodeKind.WhileStatement:
                    WriteWhileStatement((BoundWhileStatement)node, writer);
                    break;
                case BoundNodeKind.DoWhileStatement:
                    WriteDoWhileStatement((BoundDoWhileStatement)node, writer);
                    break;
                case BoundNodeKind.ForStatement:
                    WriteForStatement((BoundForStatement)node, writer);
                    break;
                case BoundNodeKind.LabelStatement:
                    WriteLabelStatement((BoundLabelStatement)node, writer);
                    break;
                case BoundNodeKind.GotoStatement:
                    WriteGotoStatement((BoundGotoStatement)node, writer);
                    break;
                case BoundNodeKind.ConditionalGotoStatement:
                    WriteConditionalGotoStatement((BoundConditionalGotoStatement)node, writer);
                    break;
                case BoundNodeKind.ExpressionStatement:
                    WriteExpressionStatement((BoundExpressionStatement)node, writer);
                    break;
                case BoundNodeKind.ErrorExpression:
                    WriteErrorExpression((BoundErrorExpression)node, writer);
                    break;
                case BoundNodeKind.LiteralExpression:
                    WriteLiteralExpression((BoundLiteralExpression)node, writer);
                    break;
                case BoundNodeKind.VariableExpression:
                    WriteVariableExpression((BoundVariableExpression)node, writer);
                    break;
                case BoundNodeKind.AssignmentExpression:
                    WriteAssignmentExpression((BoundAssignmentExpression)node, writer);
                    break;
                case BoundNodeKind.UnaryExpression:
                    WriteUnaryExpression((BoundUnaryExpression)node, writer);
                    break;
                case BoundNodeKind.BinaryExpression:
                    WriteBinaryExpression((BoundBinaryExpression)node, writer);
                    break;
                case BoundNodeKind.CallExpression:
                    WriteCallExpression((BoundCallExpression)node, writer);
                    break;
                case BoundNodeKind.ConversionExpression:
                    WriteConversionExpression((BoundConversionExpression)node, writer);
                    break;
                default:
                    throw new Exception($"Unexpected node {node.Kind}");
            }
        }

        private static void WriteNestedStaement(this IndentedTextWriter writer, BoundStatement node)
        {
            var needsIndentation = !(node is BoundBlockStatement);

            if (needsIndentation)
            {
                writer.Indent++;
            }

            node.WriteTo(writer);

            if (needsIndentation)
            {
                writer.Indent--;
            }
        }

        private static void WriteNestedExpression(this IndentedTextWriter writer, int parentPrecedence, BoundExpression expression)
        {
            if (expression is BoundUnaryExpression unaryExpression)
            {
                writer.WriteNestedExpression(parentPrecedence, SyntaxFacts.GetUnaryOperatorPrecedence(unaryExpression.Op.SyntaxKind), unaryExpression);
            }
            else if (expression is BoundBinaryExpression binaryExpression)
            {
                writer.WriteNestedExpression(parentPrecedence, SyntaxFacts.GetBinaryOperatorPrecedence(binaryExpression.Op.SyntaxKind), binaryExpression);
            }
            else
            {
                expression.WriteTo(writer);
            }
        }

        private static void WriteNestedExpression(this IndentedTextWriter writer, int parentPrecedence, int currentPrecedence, BoundExpression expression)
        {
            var needsParenthesis = parentPrecedence >= currentPrecedence;

            if (needsParenthesis)
            {
                writer.WritePunctuation(SyntaxKind.OpenParenthesisToken);
            }

            expression.WriteTo(writer);

            if (needsParenthesis)
            {
                writer.WritePunctuation(SyntaxKind.CloseParenthesisToken);
            }
        }

        private static void WriteBlockStatement(BoundBlockStatement node, IndentedTextWriter writer)
        {
            writer.WritePunctuation(SyntaxKind.OpenBraceToken);
            writer.WriteLine();
            writer.Indent++;

            foreach (var statement in node.Statements)
            {
                statement.WriteTo(writer);
            }

            writer.Indent--;
            writer.WritePunctuation(SyntaxKind.CloseBraceToken);
            writer.WriteLine();
        }

        private static void WriteVariableDeclaration(BoundVariableDeclaration node, IndentedTextWriter writer)
        {
            writer.WriteKeyword(node.Variable.IsReadOnly ? SyntaxKind.LetKeyword : SyntaxKind.VarKeyword);
            writer.WriteSpace();
            writer.WriteIdentifier(node.Variable.Name);
            writer.WriteSpace();
            writer.WritePunctuation(SyntaxKind.EqualsToken);
            writer.WriteSpace();
            node.Initializer.WriteTo(writer);
            writer.WriteLine();
        }

        private static void WriteIfStatement(BoundIfStatement node, IndentedTextWriter writer)
        {
            writer.WriteKeyword(SyntaxKind.IfKeyword);
            writer.WriteSpace();
            node.Condition.WriteTo(writer);
            writer.WriteLine();
            writer.WriteNestedStaement(node.ThenStatement);

            if (node.ElseStatement != null)
            {
                writer.WriteKeyword(SyntaxKind.ElseKeyword);
                writer.WriteLine();
                writer.WriteNestedStaement(node.ElseStatement);
            }
        }

        private static void WriteWhileStatement(BoundWhileStatement node, IndentedTextWriter writer)
        {
            writer.WriteKeyword(SyntaxKind.WhileKeyword);
            writer.WriteSpace();
            node.Condition.WriteTo(writer);
            writer.WriteLine();
            writer.WriteNestedStaement(node.Body);
        }

        private static void WriteDoWhileStatement(BoundDoWhileStatement node, IndentedTextWriter writer)
        {
            writer.WriteKeyword(SyntaxKind.DoKeyword);
            writer.WriteLine();
            writer.WriteNestedStaement(node.Body);
            writer.WriteKeyword(SyntaxKind.WhileKeyword);
            writer.WriteSpace();
            node.Condition.WriteTo(writer);
            writer.WriteLine();
        }

        private static void WriteForStatement(BoundForStatement node, IndentedTextWriter writer)
        {
            writer.WriteKeyword(SyntaxKind.ForKeyword);
            writer.WriteSpace();
            writer.WriteIdentifier(node.Variable.Name);
            writer.WriteSpace();
            writer.WritePunctuation(SyntaxKind.EqualsToken);
            writer.WriteSpace();
            node.LowerBound.WriteTo(writer);
            writer.WriteSpace();
            writer.WriteKeyword(SyntaxKind.ToKeyword);
            writer.WriteSpace();
            node.UpperBound.WriteTo(writer);
            writer.WriteLine();
            writer.WriteNestedStaement(node.Body);
        }

        private static void WriteLabelStatement(BoundLabelStatement node, IndentedTextWriter writer)
        {
            var unindent = writer.Indent > 0;

            if (unindent)
            {
                writer.Indent--;
            }

            writer.WritePunctuation(node.Label.Name);
            writer.WritePunctuation(SyntaxKind.ColonToken);
            writer.WriteLine();

            if (unindent)
            {
                writer.Indent++;
            }
        }

        private static void WriteGotoStatement(BoundGotoStatement node, IndentedTextWriter writer)
        {
            writer.WriteKeyword("goto ");
            writer.WriteIdentifier(node.Label.Name);
            writer.WriteLine();
        }

        private static void WriteConditionalGotoStatement(BoundConditionalGotoStatement node, IndentedTextWriter writer)
        {
            writer.WriteKeyword("goto ");
            writer.WriteIdentifier(node.Label.Name);
            writer.WriteKeyword(node.JumpIfTrue ? " if " : " unless ");
            node.Condition.WriteTo(writer);
            writer.WriteLine();
        }

        private static void WriteExpressionStatement(BoundExpressionStatement node, IndentedTextWriter writer)
        {
            node.Expression.WriteTo(writer);
            writer.WriteLine();
        }

        private static void WriteErrorExpression(BoundErrorExpression node, IndentedTextWriter writer)
        {
            writer.WriteKeyword("?");
        }

        private static void WriteLiteralExpression(BoundLiteralExpression node, IndentedTextWriter writer)
        {
            var value = node.Value.ToString();

            if (node.Type == TypeSymbol.Boolean)
            {
                writer.WriteKeyword((bool)node.Value ? SyntaxKind.TrueKeyword : SyntaxKind.FalseKeyword);
            }
            else if (node.Type == TypeSymbol.Interger)
            {
                writer.WriteNumber(value);
            }
            else if (node.Type == TypeSymbol.String)
            {
                value = "\"" + value.Replace("\"", "\"\"") + "\"";
                writer.WriteString(value);
            }
            else
            {
                throw new Exception($"Unexpected type {node.Type}");
            }
        }

        private static void WriteVariableExpression(BoundVariableExpression node, IndentedTextWriter writer)
        {
            writer.WriteIdentifier(node.Variable.Name);
        }

        private static void WriteAssignmentExpression(BoundAssignmentExpression node, IndentedTextWriter writer)
        {
            writer.WriteIdentifier(node.Variable.Name);
            writer.WriteSpace();
            writer.WritePunctuation(SyntaxKind.EqualsToken);
            writer.WriteSpace();
            node.Expression.WriteTo(writer);
        }

        private static void WriteUnaryExpression(BoundUnaryExpression node, IndentedTextWriter writer)
        {
            var precedence = SyntaxFacts.GetUnaryOperatorPrecedence(node.Op.SyntaxKind);

            writer.WritePunctuation(node.Op.SyntaxKind);
            writer.WriteNestedExpression(precedence, node.Operand);
        }

        private static void WriteBinaryExpression(BoundBinaryExpression node, IndentedTextWriter writer)
        {
            var precedence = SyntaxFacts.GetBinaryOperatorPrecedence(node.Op.SyntaxKind);

            writer.WriteNestedExpression(precedence, node.Left);
            writer.WriteSpace();
            writer.WritePunctuation(node.Op.SyntaxKind);
            writer.WriteSpace();
            writer.WriteNestedExpression(precedence, node.Right);
        }

        private static void WriteCallExpression(BoundCallExpression node, IndentedTextWriter writer)
        {
            writer.WriteIdentifier(node.Function.Name);
            writer.WritePunctuation(SyntaxKind.OpenParenthesisToken);

            var isFirst = true;
            foreach (var argument in node.Arguments)
            {
                if (isFirst)
                {
                    isFirst = false;
                }
                else
                {
                    writer.WritePunctuation(SyntaxKind.CommaToken);
                    writer.WriteSpace();
                }

                argument.WriteTo(writer);
            }

            writer.WritePunctuation(SyntaxKind.CloseParenthesisToken);
        }

        private static void WriteConversionExpression(BoundConversionExpression node, IndentedTextWriter writer)
        {
            writer.WriteIdentifier(node.Type.Name);
            writer.WritePunctuation(SyntaxKind.OpenParenthesisToken);
            node.Expression.WriteTo(writer);
            writer.WritePunctuation(SyntaxKind.CloseParenthesisToken);
        }
    }
}
