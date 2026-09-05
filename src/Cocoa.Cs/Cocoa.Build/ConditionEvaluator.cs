using System;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.Build
{
    /// <summary>
    /// 最小 `Condition` 表达式求值器。
    /// 语法：`'$(X)' == 'v'`、`!=`、`and`、`or`、`!`、括号（大小写不敏感）；
    /// 操作对象：`$(名称)` 属性引用、`'单引号字面量'`、裸 token。
    /// 未定义属性按空串处理（`== ''` 守卫惯用法由此生效）。
    /// </summary>
    public static class ConditionEvaluator
    {
        public static bool Evaluate(string expression, Func<string, string?> lookup)
        {
            var tokens = Tokenize(expression);
            var position = 0;
            var result = ParseOr(tokens, ref position, lookup);
            if (position != tokens.Count)
            {
                throw new ProjectFileFormatException($"unexpected token '{tokens[position].Text}' in Condition '{expression}'");
            }

            return result;
        }

        private static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            var index = 0;
            while (index < text.Length)
            {
                var ch = text[index];
                if (char.IsWhiteSpace(ch))
                {
                    index++;
                    continue;
                }

                if (ch == '\'')
                {
                    var end = text.IndexOf('\'', index + 1);
                    if (end < 0)
                    {
                        throw new ProjectFileFormatException($"unterminated string in Condition '{text}'");
                    }

                    tokens.Add(new Token(TokenKind.String, text.Substring(index + 1, end - index - 1)));
                    index = end + 1;
                    continue;
                }

                if (ch == '(')
                {
                    tokens.Add(new Token(TokenKind.ParenOpen, "("));
                    index++;
                    continue;
                }

                if (ch == ')')
                {
                    tokens.Add(new Token(TokenKind.ParenClose, ")"));
                    index++;
                    continue;
                }

                if (ch == '=')
                {
                    if (index + 1 < text.Length && text[index + 1] == '=')
                    {
                        tokens.Add(new Token(TokenKind.Equal, "=="));
                        index += 2;
                        continue;
                    }

                    throw new ProjectFileFormatException($"unexpected '=' in Condition '{text}'");
                }

                if (ch == '!')
                {
                    if (index + 1 < text.Length && text[index + 1] == '=')
                    {
                        tokens.Add(new Token(TokenKind.NotEqual, "!="));
                        index += 2;
                        continue;
                    }

                    tokens.Add(new Token(TokenKind.Not, "!"));
                    index++;
                    continue;
                }

                if (ch == '$' && index + 1 < text.Length && text[index + 1] == '(')
                {
                    var close = text.IndexOf(')', index + 2);
                    if (close < 0)
                    {
                        throw new ProjectFileFormatException($"unterminated $() in Condition '{text}'");
                    }

                    tokens.Add(new Token(TokenKind.Property, text.Substring(index + 2, close - index - 2)));
                    index = close + 1;
                    continue;
                }

                var start = index;
                while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] != '(' && text[index] != ')')
                {
                    index++;
                }

                tokens.Add(new Token(TokenKind.Word, text.Substring(start, index - start)));
            }

            return tokens;
        }

        private static bool ParseOr(IReadOnlyList<Token> tokens, ref int position, Func<string, string?> lookup)
        {
            var result = ParseAnd(tokens, ref position, lookup);
            while (MatchWord(tokens, ref position, "or"))
            {
                var right = ParseAnd(tokens, ref position, lookup);
                result = result || right;
            }

            return result;
        }

        private static bool ParseAnd(IReadOnlyList<Token> tokens, ref int position, Func<string, string?> lookup)
        {
            var result = ParseUnary(tokens, ref position, lookup);
            while (MatchWord(tokens, ref position, "and"))
            {
                var right = ParseUnary(tokens, ref position, lookup);
                result = result && right;
            }

            return result;
        }

        private static bool ParseUnary(IReadOnlyList<Token> tokens, ref int position, Func<string, string?> lookup)
        {
            if (position < tokens.Count && tokens[position].Kind == TokenKind.Not)
            {
                position++;
                return !ParseUnary(tokens, ref position, lookup);
            }

            return ParsePrimary(tokens, ref position, lookup);
        }

        private static bool ParsePrimary(IReadOnlyList<Token> tokens, ref int position, Func<string, string?> lookup)
        {
            if (position >= tokens.Count)
            {
                throw new ProjectFileFormatException("unexpected end of Condition expression");
            }

            if (tokens[position].Kind == TokenKind.ParenOpen)
            {
                position++;
                var inner = ParseOr(tokens, ref position, lookup);
                if (position >= tokens.Count || tokens[position].Kind != TokenKind.ParenClose)
                {
                    throw new ProjectFileFormatException("missing ')' in Condition expression");
                }

                position++;
                return inner;
            }

            var left = ReadValue(tokens, ref position, lookup);
            if (position < tokens.Count && (tokens[position].Kind == TokenKind.Equal || tokens[position].Kind == TokenKind.NotEqual))
            {
                var isEqual = tokens[position].Kind == TokenKind.Equal;
                position++;
                var right = ReadValue(tokens, ref position, lookup);
                return isEqual ? left == right : left != right;
            }

            // 裸真值（无比较）：非空且不敏感等于 true
            return !string.IsNullOrEmpty(left) && !left.Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadValue(IReadOnlyList<Token> tokens, ref int position, Func<string, string?> lookup)
        {
            if (position >= tokens.Count)
            {
                throw new ProjectFileFormatException("unexpected end of Condition expression");
            }

            var token = tokens[position];
            position++;
            return token.Kind switch
            {
                TokenKind.String => token.Text,
                TokenKind.Property => lookup(token.Text) ?? string.Empty,
                TokenKind.Word => token.Text,
                _ => throw new ProjectFileFormatException($"unexpected token '{token.Text}' in Condition"),
            };
        }

        private static bool MatchWord(IReadOnlyList<Token> tokens, ref int position, string word)
        {
            if (position < tokens.Count && tokens[position].Kind == TokenKind.Word &&
                tokens[position].Text.Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                position++;
                return true;
            }

            return false;
        }

        private enum TokenKind
        {
            Word,
            String,
            Property,
            Equal,
            NotEqual,
            Not,
            ParenOpen,
            ParenClose,
        }

        private readonly record struct Token(TokenKind Kind, string Text);
    }
}