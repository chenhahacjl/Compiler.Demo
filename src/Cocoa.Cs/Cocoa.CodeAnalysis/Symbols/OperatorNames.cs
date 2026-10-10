using System.Collections.Generic;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Symbols
{
    /// <summary>
    /// 运算符声明的语法/命名映射：token ↔ <see cref="OperatorKind"/> ↔ 元数据方法名（`op_Addition` 等）。
    /// 命名沿用 CLR 约定，使运算符方法在反射/互操作面与 C# 一致。
    /// </summary>
    public static class OperatorNames
    {
        private static readonly Dictionary<SyntaxKind, OperatorKind> _byToken = new()
        {
            [SyntaxKind.PlusToken] = OperatorKind.Addition,
            [SyntaxKind.PlusEqualsToken] = OperatorKind.Addition,     // `operator +=` = Addition 声明形态变体（复合赋值，C# 元数据仍 op_Addition）
            [SyntaxKind.MinusToken] = OperatorKind.Subtraction,
            [SyntaxKind.MinusEqualsToken] = OperatorKind.Subtraction, // `operator -=` 同理
            [SyntaxKind.StarToken] = OperatorKind.Multiplication,
            [SyntaxKind.SlashToken] = OperatorKind.Division,
            [SyntaxKind.PercentToken] = OperatorKind.Modulo,
            [SyntaxKind.AmpersandToken] = OperatorKind.BitwiseAnd,
            [SyntaxKind.PipeToken] = OperatorKind.BitwiseOr,
            [SyntaxKind.HatToken] = OperatorKind.BitwiseXor,
            [SyntaxKind.ShiftLeftToken] = OperatorKind.LeftShift,
            [SyntaxKind.ShiftRightToken] = OperatorKind.RightShift,
            [SyntaxKind.EqualsEqualsToken] = OperatorKind.Equality,
            [SyntaxKind.BangEqualsToken] = OperatorKind.Inequality,
            [SyntaxKind.LessToken] = OperatorKind.LessThan,
            [SyntaxKind.LessOrEqualsToken] = OperatorKind.LessThanOrEqual,
            [SyntaxKind.GreaterToken] = OperatorKind.GreaterThan,
            [SyntaxKind.GreaterOrEqualsToken] = OperatorKind.GreaterThanOrEqual,

            [SyntaxKind.BangToken] = OperatorKind.LogicalNot,
            [SyntaxKind.TildeToken] = OperatorKind.BitwiseComplement,
        };

        private static readonly Dictionary<OperatorKind, string> _names = new()
        {
            [OperatorKind.Addition] = "op_Addition",
            [OperatorKind.Subtraction] = "op_Subtraction",
            [OperatorKind.Multiplication] = "op_Multiply",
            [OperatorKind.Division] = "op_Division",
            [OperatorKind.Modulo] = "op_Modulus",
            [OperatorKind.BitwiseAnd] = "op_BitwiseAnd",
            [OperatorKind.BitwiseOr] = "op_BitwiseOr",
            [OperatorKind.BitwiseXor] = "op_ExclusiveOr",
            [OperatorKind.LeftShift] = "op_LeftShift",
            [OperatorKind.RightShift] = "op_RightShift",
            [OperatorKind.Equality] = "op_Equality",
            [OperatorKind.Inequality] = "op_Inequality",
            [OperatorKind.LessThan] = "op_LessThan",
            [OperatorKind.LessThanOrEqual] = "op_LessThanOrEqual",
            [OperatorKind.GreaterThan] = "op_GreaterThan",
            [OperatorKind.GreaterThanOrEqual] = "op_GreaterThanOrEqual",

            [OperatorKind.UnaryPlus] = "op_UnaryPlus",
            [OperatorKind.UnaryNegation] = "op_UnaryNegation",
            [OperatorKind.LogicalNot] = "op_LogicalNot",
            [OperatorKind.BitwiseComplement] = "op_OnesComplement",

            [OperatorKind.ImplicitConversion] = "op_Implicit",
            [OperatorKind.ExplicitConversion] = "op_Explicit",
        };

        /// <summary>该 <see cref="OperatorKind"/> 需要的操作数个数（1 = 一元/转换，2 = 二元）。</summary>
        public static int Arity(OperatorKind kind) => kind == OperatorKind.ImplicitConversion || kind == OperatorKind.ExplicitConversion || IsUnary(kind) ? 1 : 2;

        /// <summary>是否为用户可声明的一元运算符（转换运算符单列）。</summary>
        public static bool IsUnary(OperatorKind kind)
        {
            return kind == OperatorKind.UnaryPlus || kind == OperatorKind.UnaryNegation ||
                   kind == OperatorKind.LogicalNot || kind == OperatorKind.BitwiseComplement;
        }

        /// <summary>是否为转换运算符。</summary>
        public static bool IsConversion(OperatorKind kind)
        {
            return kind == OperatorKind.ImplicitConversion || kind == OperatorKind.ExplicitConversion;
        }

        /// <summary>比较类运算符（结果恒为 bool，绑定时据此定结果类型）。</summary>
        public static bool IsComparison(OperatorKind kind)
        {
            return kind == OperatorKind.Equality || kind == OperatorKind.Inequality ||
                   kind == OperatorKind.LessThan || kind == OperatorKind.LessThanOrEqual ||
                   kind == OperatorKind.GreaterThan || kind == OperatorKind.GreaterThanOrEqual ||
                   kind == OperatorKind.LogicalNot;
        }

        /// <summary>token → 运算符种类；不可重载的 token 返回 null。</summary>
        public static OperatorKind? FromToken(SyntaxKind kind)
        {
            // 一元 + 与 - 与二元同名，按「左右操作数同型」由绑定层区分：此处先给二元，
            // 单操作数场景由 TryGetUnary 回退到 UnaryPlus/UnaryNegation。
            return _byToken.TryGetValue(kind, out var result) ? result : (OperatorKind?)null;
        }

        /// <summary>单操作数场景的运算符种类（用于一元回退）。</summary>
        public static OperatorKind? UnaryFromToken(SyntaxKind kind)
        {
            switch (kind)
            {
                case SyntaxKind.PlusToken: return OperatorKind.UnaryPlus;
                case SyntaxKind.MinusToken: return OperatorKind.UnaryNegation;
                default: return FromToken(kind);
            }
        }

        /// <summary>运算符种类 → 元数据方法名（`op_Addition` 等）。</summary>
        public static string ToMetadataName(OperatorKind kind) => _names[kind];
    }
}
