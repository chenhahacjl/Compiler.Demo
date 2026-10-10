using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Bound;
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Cocoa.CodeAnalysis.Serialization.CoaFormat
{
    /// <summary>.coa 文本/值/摘要编解码纯函数（源 <c>CoaSerializer.Text.cs</c> 等收敛）。
    /// 不持任何状态；两端（写/读）共用以保证词形唯一。</summary>
    internal static class CoaText
    {
        public const string ChecksumTag = "sha256:";

        /// <summary>完整性校验：对正文（UTF-8）取 SHA256 小写 hex。</summary>
        public static string ComputeChecksum(string payload)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        }

        public static string RequirementName(CoaRequirement r)
        {
            return r switch
            {
                CoaRequirement.DotNet => "dotnet",
                _ => "any",
            };
        }

        public static CoaRequirement ParseRequirement(string name)
        {
            return name switch
            {
                "dotnet" => CoaRequirement.DotNet,
                _ => CoaRequirement.Any,
            };
        }

        public static string BoolWord(bool value)
        {
            return value ? "true" : "false";
        }

        public static string UnaryOpText(BoundUnaryOperatorKind kind)
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

        public static string BinaryOpText(BoundBinaryOperatorKind kind)
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

        public static BoundUnaryOperatorKind ParseUnaryOpText(string text)
        {
            return text switch
            {
                "+" => BoundUnaryOperatorKind.Identity,
                "-" => BoundUnaryOperatorKind.Negation,
                "!" => BoundUnaryOperatorKind.LogicalNegation,
                "~" => BoundUnaryOperatorKind.OnesComplement,
                _ => throw new InvalidDataException($"Unknown unary operator '{text}'"),
            };
        }

        public static BoundBinaryOperatorKind ParseBinaryOpText(string text)
        {
            return text switch
            {
                "+" => BoundBinaryOperatorKind.Addition,
                "-" => BoundBinaryOperatorKind.Subtraction,
                "*" => BoundBinaryOperatorKind.Multiplication,
                "/" => BoundBinaryOperatorKind.Division,
                "%" => BoundBinaryOperatorKind.Modulo,
                "<<" => BoundBinaryOperatorKind.ShiftLeft,
                ">>" => BoundBinaryOperatorKind.ShiftRight,
                "&" => BoundBinaryOperatorKind.BitwiseAnd,
                "|" => BoundBinaryOperatorKind.BitwiseOr,
                "^" => BoundBinaryOperatorKind.BitwiseXor,
                "==" => BoundBinaryOperatorKind.Equals,
                "!=" => BoundBinaryOperatorKind.NotEquals,
                "<" => BoundBinaryOperatorKind.Less,
                "<=" => BoundBinaryOperatorKind.LessOrEquals,
                ">" => BoundBinaryOperatorKind.Greater,
                ">=" => BoundBinaryOperatorKind.GreaterOrEquals,
                "&&" => BoundBinaryOperatorKind.LogicalAnd,
                "||" => BoundBinaryOperatorKind.LogicalOr,
                _ => throw new InvalidDataException($"Unknown binary operator '{text}'"),
            };
        }

        public static string EncodeValue(object value)
        {
            switch (value)
            {
                case null: return "n:"; // 6e-M19 M5-a：null 常量
                case int i: return "i:" + i.ToString(CultureInfo.InvariantCulture);
                case long l: return "l:" + l.ToString(CultureInfo.InvariantCulture); // 6e-M23 R8：i64 常量
                case ulong ul: return "U:" + ul.ToString(CultureInfo.InvariantCulture); // 6b：u64 常量（M0-4 随 TryParse 引入）。
                case uint ui: return "v:" + ui.ToString(CultureInfo.InvariantCulture); // 6e-M25：u32 常量
                case bool b: return "b:" + BoolWord(b);
                case char c: return "c:" + ((int)c).ToString(CultureInfo.InvariantCulture);
                case byte u: return "u:" + u.ToString(CultureInfo.InvariantCulture);
                case float f: return "f:" + f.ToString("R", CultureInfo.InvariantCulture); // 6e-M25：f32 常量
                case double d: return "d:" + d.ToString("R", CultureInfo.InvariantCulture);
                case string s: return "s:" + Escape(s);
                default:
                    throw new NotSupportedException($"Unsupported constant value type '{value.GetType()}'");
            }
        }

        public static object DecodeValue(string token)
        {
            var kind = token[0];
            var rest = token.Substring(2);
            switch (kind)
            {
                case 'n': return null!; // 6e-M19 M5-a：null 常量
                case 'i': return int.Parse(rest, CultureInfo.InvariantCulture);
                case 'l': return long.Parse(rest, CultureInfo.InvariantCulture); // 6e-M23 R8：i64 常量
                case 'b': return rest == "true";
                case 'c': return (char)int.Parse(rest, CultureInfo.InvariantCulture);
                case 'u': return (byte)int.Parse(rest, CultureInfo.InvariantCulture);
                case 'U': return ulong.Parse(rest, CultureInfo.InvariantCulture); // 6b：u64 常量
                case 'v': return uint.Parse(rest, CultureInfo.InvariantCulture); // 6e-M25：u32 常量
                case 'f': return float.Parse(rest, NumberStyles.Float, CultureInfo.InvariantCulture); // 6e-M25：f32 常量
                case 'd': return double.Parse(rest, NumberStyles.Float, CultureInfo.InvariantCulture);
                case 's': return Unescape(rest);
                default:
                    throw new InvalidDataException($"Unknown constant encoding '{token}'");
            }
        }

        public static string Escape(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case ' ': sb.Append("\\s"); break;
                    case '(': sb.Append("\\("); break;
                    case ')': sb.Append("\\)"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\0': sb.Append("\\0"); break;
                    default:
                        if (char.IsControl(c))
                        {
                            sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }

            return sb.ToString();
        }

        public static string Unescape(string text)
        {
            var sb = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (i + 1 >= text.Length)
                {
                    sb.Append('\\');
                    break;
                }

                var e = text[++i];
                switch (e)
                {
                    case '\\': sb.Append('\\'); break;
                    case 's': sb.Append(' '); break;
                    case '(': sb.Append('('); break;
                    case ')': sb.Append(')'); break;
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '0': sb.Append('\0'); break;
                    case 'u':
                        if (i + 4 < text.Length)
                        {
                            var hex = text.Substring(i + 1, 4);
                            sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                        }
                        else
                        {
                            sb.Append('u');
                        }
                        break;
                    default:
                        sb.Append(e);
                        break;
                }
            }

            return sb.ToString();
        }

        public static string Str(string text) => Escape(text);
    }
}
