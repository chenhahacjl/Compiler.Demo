using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System;
using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定一元操作符（HIR 净化）：运算符对象只携带语义 <see cref="BoundUnaryOperatorKind"/>，
    /// 不携带 <see cref="SyntaxKind"/>。前端（Binder / BoundNodeFactory / 插值拼接）经
    /// <see cref="Bind(SyntaxKind, TypeSymbol)"/> 兼容门把词法 token 翻译为语义 kind。
    /// </summary>
    public sealed class BoundUnaryOperator
    {
        private BoundUnaryOperator(BoundUnaryOperatorKind kind, TypeSymbol operandType)
            : this(kind, operandType, operandType)
        {
        }

        private BoundUnaryOperator(BoundUnaryOperatorKind kind, TypeSymbol operandType, TypeSymbol resultType)
        {
            Kind = kind;
            OperandType = operandType;
            ResultType = resultType;
        }

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
                new BoundUnaryOperator(BoundUnaryOperatorKind.LogicalNegation, TypeSymbol.Boolean),
            };

            var numericTypes = new[]
            {
                TypeSymbol.Int8, TypeSymbol.Int16, TypeSymbol.Int32, TypeSymbol.Int64,
                TypeSymbol.UInt8, TypeSymbol.UInt16, TypeSymbol.UInt32, TypeSymbol.UInt64,
                TypeSymbol.Float, TypeSymbol.Double,
            };

            foreach (var t in numericTypes)
            {
                // 6e-M21 Phase 7：<32 位整数一元 +/-/~ 结果升 Int32（C# 同构：-(byte)5 / ~(byte)5 均为 int）
                var result = t.IsInteger && t.BitWidth < 32 ? TypeSymbol.Int32 : t;
                ops.Add(new BoundUnaryOperator(BoundUnaryOperatorKind.Identity, t, result));
                ops.Add(new BoundUnaryOperator(BoundUnaryOperatorKind.Negation, t, result));

                if (t.IsInteger)
                {
                    ops.Add(new BoundUnaryOperator(BoundUnaryOperatorKind.OnesComplement, t, result));
                }
            }

            return ops.ToArray();
        }

        /// <summary>兼容词法门（HIR 净化）：token → 语义 kind 翻译后委托语义入口。</summary>
        public static BoundUnaryOperator? Bind(SyntaxKind syntaxKind, TypeSymbol operandType)
        {
            return Bind(Translate(syntaxKind), operandType);
        }

        /// <summary>语义主入口：按 <see cref="BoundUnaryOperatorKind"/> 绑定。</summary>
        public static BoundUnaryOperator? Bind(BoundUnaryOperatorKind kind, TypeSymbol operandType)
        {
            foreach (var op in _operators)
            {
                if (op.Kind == kind && op.OperandType == operandType)
                {
                    return op;
                }
            }

            return null;
        }

        /// <summary>词法 token → 语义一元 kind（HIR 净化翻译门，供 <see cref="Bind(SyntaxKind, TypeSymbol)"/>）。</summary>
        private static BoundUnaryOperatorKind Translate(SyntaxKind syntaxKind)
        {
            return syntaxKind switch
            {
                SyntaxKind.PlusToken => BoundUnaryOperatorKind.Identity,
                SyntaxKind.MinusToken => BoundUnaryOperatorKind.Negation,
                SyntaxKind.BangToken => BoundUnaryOperatorKind.LogicalNegation,
                SyntaxKind.TildeToken => BoundUnaryOperatorKind.OnesComplement,
                _ => throw new NotSupportedException($"Unsupported unary operator token '{syntaxKind}'"),
            };
        }
    }
}