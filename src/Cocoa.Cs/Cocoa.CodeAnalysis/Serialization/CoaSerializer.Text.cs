using Cocoa.CodeAnalysis.Binding;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Cocoa.CodeAnalysis.Serialization
{
    internal static partial class CoaSerializer
    {
        private static string BoolWord(bool value)
        {
            return value ? "true" : "false";
        }

        private static string UnaryOpText(BoundUnaryOperatorKind kind)
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

        private static string BinaryOpText(BoundBinaryOperatorKind kind)
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

        private static BoundUnaryOperatorKind ParseUnaryOpText(string text)
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

        private static BoundBinaryOperatorKind ParseBinaryOpText(string text)
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

        // ---------------------------------------------------------------- write: value encoding

        private static string EncodeValue(object value)
        {
            switch (value)
            {
                case null: return "n:"; // 6e-M19 M5-a：null 常量
case int i: return "i:" + i.ToString(CultureInfo.InvariantCulture);
                case long l: return "l:" + l.ToString(CultureInfo.InvariantCulture); // 6e-M23 R8：i64 常量
                case ulong ul: return "U:" + ul.ToString(CultureInfo.InvariantCulture); // 6b：u64 常量（M0-4 随 TryParse 引入）。
                case bool b: return "b:" + (b ? 1 : 0);
                case char c: return "c:" + ((int)c).ToString(CultureInfo.InvariantCulture);
                case byte u: return "u:" + u.ToString(CultureInfo.InvariantCulture);
                case double d: return "d:" + d.ToString("R", CultureInfo.InvariantCulture);
                case string s: return "s:" + Escape(s);
                default:
                    throw new NotSupportedException($"Unsupported constant value type '{value.GetType()}'");
            }
        }

        private static object DecodeValue(string token)
        {
            var kind = token[0];
            var rest = token.Substring(2);
            switch (kind)
            {
                case 'n': return null!; // 6e-M19 M5-a：null 常量
                case 'i': return int.Parse(rest, CultureInfo.InvariantCulture);
                case 'l': return long.Parse(rest, CultureInfo.InvariantCulture); // 6e-M23 R8：i64 常量
                case 'b': return rest == "1";
                case 'c': return (char)int.Parse(rest, CultureInfo.InvariantCulture);
                case 'u': return (byte)int.Parse(rest, CultureInfo.InvariantCulture);
                case 'U': return ulong.Parse(rest, CultureInfo.InvariantCulture); // 6b：u64 常量
                case 'd': return double.Parse(rest, NumberStyles.Float, CultureInfo.InvariantCulture);
                case 's': return Unescape(rest);
                default:
                    throw new InvalidDataException($"Unknown constant encoding '{token}'");
            }
        }

        // ---------------------------------------------------------------- write: string escaping

        private static string Escape(string text)
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

        private static string Unescape(string text)
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

        private static string Str(string text) => Escape(text);

        // ---------------------------------------------------------------- write: helpers

    }
}
