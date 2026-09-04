using Cocoa.CodeAnalysis.Syntax;
using System;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 语义运算符文本助手（HIR 净化 T0.4）：由 <see cref="BoundBinaryOperatorKind"/> / <see cref="BoundUnaryOperatorKind"/>
    /// 直接产出 glyph 与优先级——供打印器（BoundNodePrinter）、IL 默认诊断臂与 .coa 编解码（CoaSerializer）单一真相源。
    /// glyph 字符串与旧 SyntaxKind 形态逐字一致（.coa 文本兼容）；优先级数值与 <see cref="SyntaxFacts"/> 现行表一致。
    /// </summary>
    public static class BoundOperatorText
    {
        /// <summary>二元运算符符号（对齐 CoaSerializer.BinaryOpText / SyntaxFacts.GetText 的旧映射，含 Reference* 并入 &amp;==/!=）。</summary>
        public static string BinaryGlyph(BoundBinaryOperatorKind kind)
        {
            return kind switch
            {
                BoundBinaryOperatorKind.Addition => "+",
                BoundBinaryOperatorKind.Subtraction => "-",
                BoundBinaryOperatorKind.Multiplication => "*",
                BoundBinaryOperatorKind.Division => "/",
                BoundBinaryOperatorKind.Modulo => "%",
                BoundBinaryOperatorKind.ShiftLeft => "<<",
                BoundBinaryOperatorKind.ShiftRight => ">>",
                BoundBinaryOperatorKind.BitwiseAnd => "&",
                BoundBinaryOperatorKind.BitwiseOr => "|",
                BoundBinaryOperatorKind.BitwiseXor => "^",
                BoundBinaryOperatorKind.Equals => "==",
                BoundBinaryOperatorKind.NotEquals => "!=",
                BoundBinaryOperatorKind.ReferenceEquals => "==",
                BoundBinaryOperatorKind.ReferenceNotEquals => "!=",
                BoundBinaryOperatorKind.Less => "<",
                BoundBinaryOperatorKind.LessOrEquals => "<=",
                BoundBinaryOperatorKind.Greater => ">",
                BoundBinaryOperatorKind.GreaterOrEquals => ">=",
                BoundBinaryOperatorKind.LogicalAnd => "&&",
                BoundBinaryOperatorKind.LogicalOr => "||",
                _ => throw new NotSupportedException($"Unsupported binary operator '{kind}'"),
            };
        }

        /// <summary>一元运算符符号（对齐 CoaSerializer.UnaryOpText / SyntaxFacts.GetText 的旧映射）。</summary>
        public static string UnaryGlyph(BoundUnaryOperatorKind kind)
        {
            return kind switch
            {
                BoundUnaryOperatorKind.Identity => "+",
                BoundUnaryOperatorKind.Negation => "-",
                BoundUnaryOperatorKind.LogicalNegation => "!",
                BoundUnaryOperatorKind.OnesComplement => "~",
                _ => throw new NotSupportedException($"Unsupported unary operator '{kind}'"),
            };
        }

        /// <summary>二元运算符优先级（镜像 SyntaxFacts.GetBinaryOperatorPrecedence 的语义表）。</summary>
        public static int BinaryPrecedence(BoundBinaryOperatorKind kind)
        {
            return kind switch
            {
                BoundBinaryOperatorKind.Multiplication
                or BoundBinaryOperatorKind.Division
                or BoundBinaryOperatorKind.Modulo => 5,

                BoundBinaryOperatorKind.Addition
                or BoundBinaryOperatorKind.Subtraction
                or BoundBinaryOperatorKind.ShiftLeft
                or BoundBinaryOperatorKind.ShiftRight => 4,

                BoundBinaryOperatorKind.Equals
                or BoundBinaryOperatorKind.NotEquals
                or BoundBinaryOperatorKind.ReferenceEquals
                or BoundBinaryOperatorKind.ReferenceNotEquals
                or BoundBinaryOperatorKind.Less
                or BoundBinaryOperatorKind.LessOrEquals
                or BoundBinaryOperatorKind.Greater
                or BoundBinaryOperatorKind.GreaterOrEquals => 3,

                BoundBinaryOperatorKind.BitwiseAnd
                or BoundBinaryOperatorKind.LogicalAnd => 2,

                BoundBinaryOperatorKind.BitwiseOr
                or BoundBinaryOperatorKind.BitwiseXor
                or BoundBinaryOperatorKind.LogicalOr => 1,

                _ => 0,
            };
        }

        /// <summary>一元运算符优先级（镜像 SyntaxFacts.GetUnaryOperatorPrecedence 的语义表，一律 6）。</summary>
        public static int UnaryPrecedence(BoundUnaryOperatorKind kind)
        {
            return kind switch
            {
                BoundUnaryOperatorKind.Identity
                or BoundUnaryOperatorKind.Negation
                or BoundUnaryOperatorKind.LogicalNegation
                or BoundUnaryOperatorKind.OnesComplement => 6,
                _ => 0,
            };
        }
    }
}