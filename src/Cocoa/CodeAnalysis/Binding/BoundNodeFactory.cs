using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Cocoa.CodeAnalysis.Binding
{
    internal static class BoundNodeFactory
    {
        public static BoundBlockStatement Block(SyntaxNode syntax, params BoundStatement[] statements)
        {
            return new BoundBlockStatement(syntax, ImmutableArray.Create(statements));
        }

        public static BoundVariableDeclaration VariableDeclaration(SyntaxNode syntax, VariableSymbol symbol, BoundExpression initializer)
        {
            return new BoundVariableDeclaration(syntax, symbol, initializer);
        }

        public static BoundVariableDeclaration VariableDeclaration(SyntaxNode syntax, string name, BoundExpression initializer)
        {
            return VariableDeclarationInternal(syntax, name, initializer, isReadOnly: false);
        }

        public static BoundVariableDeclaration ConstantDeclaration(SyntaxNode syntax, string name, BoundExpression initializer)
        {
            return VariableDeclarationInternal(syntax, name, initializer, isReadOnly: true);
        }

        private static BoundVariableDeclaration VariableDeclarationInternal(SyntaxNode syntax, string name, BoundExpression initializer, bool isReadOnly)
        {
            var local = new LocalVariableSymbol(name, isReadOnly, initializer.Type, initializer.ConstantValue);

            return new BoundVariableDeclaration(syntax, local, initializer);
        }

        public static BoundWhileStatement While(SyntaxNode syntax, BoundExpression condition, BoundStatement body, BoundLabel breakLabel, BoundLabel continueLabel)
        {
            return new BoundWhileStatement(syntax, condition, body, breakLabel, continueLabel);
        }

        public static BoundGotoStatement Goto(SyntaxNode syntax, BoundLabel label)
        {
            return new BoundGotoStatement(syntax, label);
        }

        public static BoundGotoStatement Goto(SyntaxNode syntax, BoundLabelStatement label)
        {
            return new BoundGotoStatement(syntax, label.Label);
        }

        public static BoundConditionalGotoStatement GotoIf(SyntaxNode syntax, BoundLabelStatement label, BoundExpression condition, bool jumpIfTrue)
        {
            return new BoundConditionalGotoStatement(syntax, label.Label, condition, jumpIfTrue);
        }

        public static BoundConditionalGotoStatement GotoTrue(SyntaxNode syntax, BoundLabelStatement label, BoundExpression condition)
        {
            return GotoIf(syntax, label, condition, jumpIfTrue: true);
        }

        public static BoundConditionalGotoStatement GotoFalse(SyntaxNode syntax, BoundLabelStatement label, BoundExpression condition)
        {
            return GotoIf(syntax, label, condition, jumpIfTrue: false);
        }

        public static BoundLabelStatement Label(SyntaxNode syntax, BoundLabel label)
        {
            return new BoundLabelStatement(syntax, label);
        }

        public static BoundNopStatement Nop(SyntaxNode syntax)
        {
            return new BoundNopStatement(syntax);
        }

        public static BoundAssignmentExpression Assignment(SyntaxNode syntax, VariableSymbol variable, BoundExpression expression)
        {
            return new BoundAssignmentExpression(syntax, variable, expression);
        }

        public static BoundBinaryExpression Binary(SyntaxNode syntax, BoundExpression left, SyntaxKind kind, BoundExpression right)
        {
            var op = BoundBinaryOperator.Bind(kind, left.Type, right.Type)!;

            return Binary(syntax, left, op, right);
        }

        public static BoundBinaryExpression Binary(SyntaxNode syntax, BoundExpression left, BoundBinaryOperator op, BoundExpression right)
        {
            return new BoundBinaryExpression(syntax, left, op, right);
        }

        public static BoundBinaryExpression Add(SyntaxNode syntax, BoundExpression left, BoundExpression right)
        {
            return Binary(syntax, left, SyntaxKind.PlusToken, right);
        }

        public static BoundBinaryExpression LessOrEqual(SyntaxNode syntax, BoundExpression left, BoundExpression right)
        {
            return Binary(syntax, left, SyntaxKind.LessOrEqualsToken, right);
        }

        public static BoundExpressionStatement Increment(SyntaxNode syntax, BoundVariableExpression variable)
        {
            var increment = Add(syntax, variable, Literal(syntax, 1));
            var incrementAssign = new BoundAssignmentExpression(syntax, variable.Variable, increment);

            return new BoundExpressionStatement(syntax, incrementAssign);
        }

        public static BoundUnaryExpression Not(SyntaxNode syntax, BoundExpression condition)
        {
            Debug.Assert(condition.Type == TypeSymbol.Boolean);

            var op = BoundUnaryOperator.Bind(SyntaxKind.BangToken, TypeSymbol.Boolean);

            Debug.Assert(op != null);

            return new BoundUnaryExpression(syntax, op, condition);
        }

        public static BoundVariableExpression Variable(SyntaxNode syntax, BoundVariableDeclaration variable)
        {
            return Variable(syntax, variable.Variable);
        }

        public static BoundVariableExpression Variable(SyntaxNode syntax, VariableSymbol variable)
        {
            return new BoundVariableExpression(syntax, variable);
        }

        public static BoundLiteralExpression Literal(SyntaxNode syntax, object literal)
        {
            Debug.Assert(literal is string || literal is bool || literal is int);

            return new BoundLiteralExpression(syntax, literal);
        }
    }
}