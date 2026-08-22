using System.Collections.Generic;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定一元操作符
    /// </summary>
    internal sealed class BoundUnaryOperator
    {
        private BoundUnaryOperator(SyntaxKind syntaxKind, BoundUnaryOperatorKind kind, TypeSymbol operandType)
            : this(syntaxKind, kind, operandType, operandType)
        {
        }

        private BoundUnaryOperator(SyntaxKind syntaxKind, BoundUnaryOperatorKind kind, TypeSymbol operandType, TypeSymbol resultType)
        {
            SyntaxKind = syntaxKind;
            Kind = kind;
            OperandType = operandType;
            ResultType = resultType;
        }

        public SyntaxKind SyntaxKind { get; }
        public BoundUnaryOperatorKind Kind { get; }
        public TypeSymbol OperandType { get; }
        public TypeSymbol ResultType { get; }

        private static readonly BoundUnaryOperator[] _operators = BuildOperators();

        /// <summary>
        /// 6e-M21 Phase 1：程序化生成一元运算符表——整数类型支持 +x / -x / ~x，浮点支持 +x / -x。
        /// </summary>
        private static BoundUnaryOperator[] BuildOperators()
        {
            var ops = new List<BoundUnaryOperator>
            {
                new BoundUnaryOperator(SyntaxKind.BangToken, BoundUnaryOperatorKind.LogicalNegation, TypeSymbol.Boolean),
            };

            var numericTypes = new[]
            {
                TypeSymbol.Int8, TypeSymbol.Int16, TypeSymbol.Int32, TypeSymbol.Int64,
                TypeSymbol.UInt8, TypeSymbol.UInt16, TypeSymbol.UInt32, TypeSymbol.UInt64,
                TypeSymbol.Float, TypeSymbol.Double,
            };

            foreach (var t in numericTypes)
            {
                ops.Add(new BoundUnaryOperator(SyntaxKind.PlusToken, BoundUnaryOperatorKind.Identity, t));
                ops.Add(new BoundUnaryOperator(SyntaxKind.MinusToken, BoundUnaryOperatorKind.Negation, t));

                if (t.IsInteger)
                {
                    ops.Add(new BoundUnaryOperator(SyntaxKind.TildeToken, BoundUnaryOperatorKind.OnesComplement, t));
                }
            }

            return ops.ToArray();
        }

        public static BoundUnaryOperator? Bind(SyntaxKind syntaxKind, TypeSymbol operandType)
        {
            foreach (var op in _operators)
            {
                if (op.SyntaxKind == syntaxKind && op.OperandType == operandType)
                {
                    return op;
                }
            }

            return null;
        }
    }
}
