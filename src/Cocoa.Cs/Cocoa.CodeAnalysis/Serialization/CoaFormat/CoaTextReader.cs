using System;
using System.Globalization;
using System.IO;

namespace Cocoa.CodeAnalysis.Serialization.CoaFormat
{
    /// <summary>.coa 文本读取器：token 流 + 原子/结构括号推进（源 <c>CoaSerializer.Reader</c> 提升；
    /// 收敛 <c>ReadLabeledField/ReadCountField/ParseBoolWord</c> 为实例方法）。</summary>
    internal sealed class CoaTextReader
    {
        private readonly string[] _tokens;
        private int _pos;

        public CoaTextReader(string[] tokens)
        {
            _tokens = tokens;
        }

        public string Expect(string kind)
        {
            var token = Next();
            if (token != kind)
            {
                throw new InvalidDataException($"Expected '{kind}' but found '{token}'");
            }

            return token;
        }

        public string ExpectKind()
        {
            var token = Next();
            if (token == "(" || token == ")")
            {
                throw new InvalidDataException($"Expected kind token but found '{token}'");
            }

            return token;
        }

        public string ExpectString()
        {
            var token = Next();
            if (token == "(" || token == ")")
            {
                throw new InvalidDataException($"Expected atom but found '{token}'");
            }

            return token;
        }

        public int ExpectInt()
        {
            var token = ExpectString();
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                throw new InvalidDataException($"Expected integer but found '{token}'");
            }

            return value;
        }

        /// <summary>窥探当前原始 token（不跳过 `(`）——用于判断子节点是否出现。</summary>
        public string PeekRaw()
        {
            return _pos < _tokens.Length ? _tokens[_pos] : "";
        }

        public bool TryExpect(out string token)
        {
            // 跳过节点开括号 `(`
            while (_pos < _tokens.Length && _tokens[_pos] == "(")
            {
                _pos++;
            }

            if (_pos >= _tokens.Length)
            {
                token = null!;
                return false;
            }

            // `)` 不消费（留给 End()），返回 false 终止当前列表
            if (_tokens[_pos] == ")")
            {
                token = ")";
                return false;
            }

            token = _tokens[_pos++];
            return true;
        }

        public void End()
        {
            // 当前 token 应为节点闭括号 `)`（直接消费，不跳过 `(`）。
            if (_pos >= _tokens.Length)
            {
                throw new InvalidDataException($"unexpected end of .coa file at pos {_pos}; context: {Context()}");
            }

            var token = _tokens[_pos++];
            if (token != ")")
            {
                throw new InvalidDataException($"Expected ')' but found '{token}' at pos {_pos - 1}; context: {Context()}");
            }
        }

        /// <summary>解析 `true`/`false` 词形布尔（对齐全格式统一后的词形；严格校验）。</summary>
        public bool ParseBoolWord(string text)
        {
            return text switch
            {
                "true" => true,
                "false" => false,
                _ => throw new InvalidDataException($"Expected 'true'/'false' but found '{text}'"),
            };
        }

        /// <summary>读取 label:value 形式的字段并校验标签。</summary>
        public string ReadLabeledField(string label)
        {
            var token = ExpectString();
            if (!token.StartsWith(label, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Expected field '{label}' but found '{token}'");
            }

            return CoaText.Unescape(token.Substring(label.Length));
        }

        /// <summary>读取 count:N 形式的计数字段。</summary>
        public int ReadCountField(string label)
        {
            var token = ExpectString();
            if (!token.StartsWith(label, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Expected field '{label}' but found '{token}'");
            }

            return int.Parse(token.Substring(label.Length), CultureInfo.InvariantCulture);
        }

        private string Context()
        {
            var start = Math.Max(0, _pos - 12);
            var count = Math.Min(_tokens.Length - start, 24);
            return string.Join(" ", _tokens, start, count);
        }

        private string Next()
        {
            // 跳过节点开括号 `(`；返回原子或 `)`（列表终止）
            while (true)
            {
                if (_pos >= _tokens.Length)
                {
                    throw new InvalidDataException("unexpected end of .coa file");
                }

                var token = _tokens[_pos++];
                if (token != "(")
                {
                    return token;
                }
            }
        }
    }
}
