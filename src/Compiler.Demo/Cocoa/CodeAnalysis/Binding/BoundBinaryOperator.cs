using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Linq;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定二元操作符
    /// </summary>
    internal sealed class BoundBinaryOperator
    {
        private BoundBinaryOperator(SyntaxKind syntaxKind, BoundBinaryOperatorKind kind, TypeSymbol type)
            : this(syntaxKind, kind, type, type, type)
        {
        }

        private BoundBinaryOperator(SyntaxKind syntaxKind, BoundBinaryOperatorKind kind, TypeSymbol operandType, TypeSymbol resultType)
            : this(syntaxKind, kind, operandType, operandType, resultType)
        {
        }

        private BoundBinaryOperator(SyntaxKind syntaxKind, BoundBinaryOperatorKind kind, TypeSymbol leftType, TypeSymbol rightType, TypeSymbol resultType)
        {
            SyntaxKind = syntaxKind;
            Kind = kind;
            LeftType = leftType;
            RightType = rightType;
            ResultType = resultType;
        }

        public SyntaxKind SyntaxKind { get; }
        public BoundBinaryOperatorKind Kind { get; }
        public TypeSymbol LeftType { get; }
        public TypeSymbol RightType { get; }
        public TypeSymbol ResultType { get; }

        private static BoundBinaryOperator[] m_operators =
        {
            new BoundBinaryOperator(SyntaxKind.PlusToken, BoundBinaryOperatorKind.Addition, TypeSymbol.Interger),
            new BoundBinaryOperator(SyntaxKind.MinusToken, BoundBinaryOperatorKind.Subtraction, TypeSymbol.Interger),
            new BoundBinaryOperator(SyntaxKind.StarToken, BoundBinaryOperatorKind.Multiplication, TypeSymbol.Interger),
            new BoundBinaryOperator(SyntaxKind.SlashToken, BoundBinaryOperatorKind.Division, TypeSymbol.Interger),
            new BoundBinaryOperator(SyntaxKind.AmpersandToken, BoundBinaryOperatorKind.BitwiseAnd, TypeSymbol.Interger),
            new BoundBinaryOperator(SyntaxKind.PipeToken, BoundBinaryOperatorKind.BitwiseOr, TypeSymbol.Interger),
            new BoundBinaryOperator(SyntaxKind.HatToken, BoundBinaryOperatorKind.BitwiseXor, TypeSymbol.Interger),
            new BoundBinaryOperator(SyntaxKind.EqualsEqualsToken, BoundBinaryOperatorKind.Equals, TypeSymbol.Interger, TypeSymbol.Boolean),
            new BoundBinaryOperator(SyntaxKind.BangEqualsToken, BoundBinaryOperatorKind.NotEquals, TypeSymbol.Interger, TypeSymbol.Boolean),
            new BoundBinaryOperator(SyntaxKind.LessToken, BoundBinaryOperatorKind.Less, TypeSymbol.Interger, TypeSymbol.Boolean),
            new BoundBinaryOperator(SyntaxKind.LessOrEqualsToken, BoundBinaryOperatorKind.LessOrEquals, TypeSymbol.Interger, TypeSymbol.Boolean),
            new BoundBinaryOperator(SyntaxKind.GreaterToken, BoundBinaryOperatorKind.Greater, TypeSymbol.Interger, TypeSymbol.Boolean),
            new BoundBinaryOperator(SyntaxKind.GreaterOrEqualsToken, BoundBinaryOperatorKind.GreaterOrEquals, TypeSymbol.Interger, TypeSymbol.Boolean),

            new BoundBinaryOperator(SyntaxKind.AmpersandToken, BoundBinaryOperatorKind.BitwiseAnd, TypeSymbol.Boolean),
            new BoundBinaryOperator(SyntaxKind.AmpersandAmpersandToken, BoundBinaryOperatorKind.LogicalAnd, TypeSymbol.Boolean),
            new BoundBinaryOperator(SyntaxKind.PipeToken, BoundBinaryOperatorKind.BitwiseOr, TypeSymbol.Boolean),
            new BoundBinaryOperator(SyntaxKind.PipePipeToken, BoundBinaryOperatorKind.LogicalOr, TypeSymbol.Boolean),
            new BoundBinaryOperator(SyntaxKind.HatToken, BoundBinaryOperatorKind.BitwiseXor, TypeSymbol.Boolean),
            new BoundBinaryOperator(SyntaxKind.EqualsEqualsToken, BoundBinaryOperatorKind.Equals, TypeSymbol.Boolean, TypeSymbol.Boolean),
            new BoundBinaryOperator(SyntaxKind.BangEqualsToken, BoundBinaryOperatorKind.NotEquals, TypeSymbol.Boolean, TypeSymbol.Boolean),

            new BoundBinaryOperator(SyntaxKind.PlusToken, BoundBinaryOperatorKind.Addition, TypeSymbol.String),
            new BoundBinaryOperator(SyntaxKind.EqualsEqualsToken, BoundBinaryOperatorKind.Equals, TypeSymbol.String, TypeSymbol.Boolean),
            new BoundBinaryOperator(SyntaxKind.BangEqualsToken, BoundBinaryOperatorKind.NotEquals, TypeSymbol.String, TypeSymbol.Boolean),
        };

        public static BoundBinaryOperator Bind(SyntaxKind syntaxKind, TypeSymbol leftType, TypeSymbol rightType)
        {
            return m_operators.FirstOrDefault(op => op.SyntaxKind == syntaxKind && op.LeftType == leftType && op.RightType == rightType);
        }
    }
}
