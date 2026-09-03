using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 词法分析器 (Lexical Analyzer)
    /// <br/>
    /// 字符 => Token
    /// </summary>
    internal abstract partial class LexerBase : ILexer
    {
        private void ReadString()
        {
            // "Test \" String"
            // "Test "" String"
            // "Test \n String"

            // 跳过当前引号
            _position++;

            var stringBuilder = new StringBuilder();
            var done = false;

            while (!done)
            {
                switch (Current)
                {
                    case '\0':
                    case '\r':
                    case '\n':
                    {
                        var span = new TextSpan(_start, 1);
                        var location = new TextLocation(_text, span);
                        _diagnostics.ReportUnterminatedString(location);
                        done = true;

                        break;
                    }
                    case '"':
                    {
                        if (Lookahead == '"')
                        {
                            stringBuilder.Append(Current);
                            _position += 2;
                        }
                        else
                        {
                            _position++;
                            done = true;
                        }

                        break;
                    }
                    case '\\':
                    {
                        ReadEscape(stringBuilder);
                        break;
                    }
                    default:
                    {
                        stringBuilder.Append(Current);
                        _position++;

                        break;
                    }
                }
            }

            _kind = SyntaxKind.StringToken;
            _value = stringBuilder.ToString();
        }

        /// <summary>
        /// verbatim 字符串 <c>@"..."</c>：不处理 <c>\</c> 转义，<c>""</c> 转义引号；允许多行（原样保留）。
        /// </summary>
        private void ReadVerbatimString()
        {
            // 当前位置在 '@'：跳过 '@'
            _position++;
            // 跳过开头引号
            _position++;

            var stringBuilder = new StringBuilder();
            var done = false;

            while (!done)
            {
                switch (Current)
                {
                    case '\0':
                    {
                        var span = new TextSpan(_start, 1);
                        var location = new TextLocation(_text, span);
                        _diagnostics.ReportUnterminatedString(location);
                        done = true;
                        break;
                    }
                    case '"':
                    {
                        if (Lookahead == '"')
                        {
                            stringBuilder.Append(Current);
                            _position += 2;
                        }
                        else
                        {
                            _position++;
                            done = true;
                        }
                        break;
                    }
                    default:
                    {
                        // 多行：\r/\n 原样保留
                        stringBuilder.Append(Current);
                        _position++;
                        break;
                    }
                }
            }

            _kind = SyntaxKind.VerbatimStringToken;
            _value = stringBuilder.ToString();
        }

        /// <summary>
        /// raw 字符串 <c>"""..."""</c>：定界符 = 起始最大引号串 N（≥3），内容直到同长 N 引号串闭合；
        /// 不处理转义；多行按闭合定界符所在列剥离每行前导空白。注：全引号空串 <c>""""""</c>（C# 合法）此处报未终止。
        /// </summary>
        private void ReadRawString()
        {
            // 当前位置在 '"'：统计起始引号串
            var delimiterStart = _position;
            var delimiter = 0;
            while (Current == '"')
            {
                delimiter++;
                _position++;
            }

            if (delimiter < 3)
            {
                // 由分派保证不会走到，防御性回退为普通字符串
                _kind = SyntaxKind.BadToken;
                return;
            }

            var stringBuilder = new StringBuilder();
            var contentStart = _position;
            var done = false;

            while (!done)
            {
                if (Current == '"')
                {
                    // 统计引号串
                    var runStart = _position;
                    var run = 0;
                    while (Current == '"')
                    {
                        run++;
                        _position++;
                    }

                    if (run >= delimiter)
                    {
                        // 同长 N 闭合（更长的引号串以 N 结尾也闭合，多余部分视为内容前引号——贪心规则取首次 N 连引号）。
                        _position = runStart + delimiter;
                        done = true;
                    }
                    else
                    {
                        stringBuilder.Append(new string('"', run));
                    }
                }
                else if (Current == '\0')
                {
                    var span = new TextSpan(delimiterStart, 1);
                    var location = new TextLocation(_text, span);
                    _diagnostics.ReportUnterminatedString(location);
                    done = true;
                }
                else
                {
                    stringBuilder.Append(Current);
                    _position++;
                }
            }

            // 多行缩进剥离（仅内容跨行时）：闭合定界符所在列 = 每行剥离的前导空白数
            if (_text.GetLineIndex(contentStart) < _text.GetLineIndex(_position))
            {
                // C# 11：开定界符后紧跟的换行不计入内容
                if (stringBuilder.Length > 0 && (stringBuilder[0] == '\r' || stringBuilder[0] == '\n'))
                {
                    var remove = stringBuilder[0] == '\r' && stringBuilder.Length > 1 && stringBuilder[1] == '\n' ? 2 : 1;
                    stringBuilder.Remove(0, remove);
                }

                var closingLineIndex = _text.GetLineIndex(_position);
                var closingLine = _text.Lines[closingLineIndex];
                var indent = _position - closingLine.Start; // 闭合定界符在该行的列
                if (indent > 0)
                {
                    var result = new StringBuilder();
                    var atLineStart = true;
                    var consumedOnLine = 0;
                    for (var i = 0; i < stringBuilder.Length; i++)
                    {
                        var c = stringBuilder[i];
                        if (atLineStart)
                        {
                            if ((c == ' ' || c == '\t') && consumedOnLine < indent)
                            {
                                consumedOnLine++;
                                continue;
                            }

                            atLineStart = false;
                        }

                        result.Append(c);
                        if (c == '\r' || c == '\n')
                        {
                            atLineStart = true;
                            consumedOnLine = 0;
                        }
                    }

                    stringBuilder = result;
                }

                // C# 11：闭合定界符所在行之前结尾的换行不计入内容
                if (stringBuilder.Length > 0 && stringBuilder[^1] == '\n')
                {
                    var remove = stringBuilder.Length > 1 && stringBuilder[^2] == '\r' ? 2 : 1;
                    stringBuilder.Remove(stringBuilder.Length - remove, remove);
                }
            }

            _kind = SyntaxKind.RawStringToken;
            _value = stringBuilder.ToString();
        }

        /// <summary>处理一个转义序列（当前位置在 <c>\</c>），追加结果到 builder 并推进位置。</summary>
        private void ReadEscape(StringBuilder stringBuilder)
        {
            var escapeStart = _position;
            _position++; // 消费 '\'
            switch (Current)
            {
                case 'n': stringBuilder.Append('\n'); _position++; break;
                case 't': stringBuilder.Append('\t'); _position++; break;
                case 'r': stringBuilder.Append('\r'); _position++; break;
                case '0': stringBuilder.Append('\0'); _position++; break;
                case '\\': stringBuilder.Append('\\'); _position++; break;
                case '\'': stringBuilder.Append('\''); _position++; break;
                case '"': stringBuilder.Append('"'); _position++; break;
                case 'u':
                    {
                        _position++;
                        if (IsHexDigit(Current) && IsHexDigit(Peek(1)) && IsHexDigit(Peek(2)) && IsHexDigit(Peek(3)))
                        {
                            var hex = _text.ToString(_position, 4);
                            stringBuilder.Append((char)Convert.ToInt32(hex, 16));
                            _position += 4;
                        }
                        else
                        {
                            _diagnostics.ReportUnrecognizedEscape(new TextLocation(_text, new TextSpan(escapeStart, _position - escapeStart + 1)), "u");
                        }
                        break;
                    }
                case 'x':
                    {
                        _position++;
                        var hexDigits = 0;
                        while (hexDigits < 4 && IsHexDigit(Current))
                        {
                            hexDigits++;
                            _position++;
                        }

                        if (hexDigits == 0)
                        {
                            _diagnostics.ReportUnrecognizedEscape(new TextLocation(_text, new TextSpan(escapeStart, _position - escapeStart + 1)), "x");
                        }
                        else
                        {
                            var hex = _text.ToString(_position - hexDigits, hexDigits);
                            stringBuilder.Append((char)Convert.ToInt32(hex, 16));
                        }
                        break;
                    }
                case 'U':
                    {
                        _position++;
                        if (IsHexDigit(Current) && IsHexDigit(Peek(1)) && IsHexDigit(Peek(2)) && IsHexDigit(Peek(3)) &&
                            IsHexDigit(Peek(4)) && IsHexDigit(Peek(5)) && IsHexDigit(Peek(6)) && IsHexDigit(Peek(7)))
                        {
                            var hex = _text.ToString(_position, 8);
                            var codePoint = Convert.ToInt32(hex, 16);
                            if (codePoint > 0x10FFFF)
                            {
                                _diagnostics.ReportUnrecognizedEscape(new TextLocation(_text, new TextSpan(escapeStart, _position - escapeStart + 1)), "U");
                            }
                            else
                            {
                                _position += 8;
                                stringBuilder.Append(char.ConvertFromUtf32(codePoint));
                            }
                        }
                        else
                        {
                            _diagnostics.ReportUnrecognizedEscape(new TextLocation(_text, new TextSpan(escapeStart, _position - escapeStart + 1)), "U");
                        }
                        break;
                    }
                default:
                    {
                        _diagnostics.ReportUnrecognizedEscape(new TextLocation(_text, new TextSpan(escapeStart, _position - escapeStart + 1)), Current.ToString());
                        stringBuilder.Append('\\');
                        break;
                    }
            }
        }

        /// <summary>
        /// 插值字符串 <c>$"..."</c> / <c>$@"..."</c> / <c>@$"..."</c>：切分为字面量文本段与洞（<c>{expr}</c>）。
        /// 洞携带源文本与绝对 Span（含洞内字符串中的 <c>{</c>/<c>}</c> 跳过），交 Parser 逐洞子解析并保证诊断定位。
        /// verbatim 模式（含 <c>@</c> 前缀）：字面量/洞允许换行原样保留、不处理 <c>\</c> 转义。
        /// 普通模式：单行、字面量段处理 <c>\</c> 转义。
        /// </summary>
        private void ReadInterpolatedString(bool verbatim)
        {
            var parts = new List<InterpolatedStringPart>();
            var literal = new StringBuilder();

            // 消费前缀（$ 或 @，再配对前缀（@ / @$），再开头引号
            _position++; // 消费 '$' 或 '@'
            if (Current == '@' || Current == '$')
            {
                _position++;
            }
            _position++; // 消费开头引号

            var literalStart = _position;

            var done = false;
            while (!done)
            {
                if (Current == '\0' || (!verbatim && (Current == '\r' || Current == '\n')))
                {
                    var span = new TextSpan(_start, 1);
                    var location = new TextLocation(_text, span);
                    _diagnostics.ReportUnterminatedString(location);
                    done = true;
                    break;
                }

                switch (Current)
                {
                    case '"':
                    {
                        if (Lookahead == '"')
                        {
                            literal.Append(Current);
                            _position += 2;
                        }
                        else
                        {
                            _position++;
                            done = true;
                        }
                        break;
                    }
                    case '{':
                    {
                        if (Lookahead == '{')
                        {
                            literal.Append('{');
                            _position += 2;
                        }
                        else
                        {
                            // 冲刷字面量段
                            if (literal.Length > 0)
                            {
                                parts.Add(new InterpolatedStringPart(InterpolatedStringPartKind.Literal, literal.ToString(), literalStart, _position));
                                literal.Clear();
                            }

                            // 扫描洞到匹配 '}'（跳过洞内字符串中的 '}'/'{'；verbatim 放行换行）
                            var holeStart = _position + 1;
                            _position++; // 跳过 '{'
                            var depth = 1;
                            var holeText = new StringBuilder();
                            while (depth > 0 && Current != '\0' && !(!verbatim && (Current == '\r' || Current == '\n')))
                            {
                                if (Current == '{')
                                {
                                    depth++;
                                    holeText.Append(Current);
                                    _position++;
                                }
                                else if (Current == '}')
                                {
                                    depth--;
                                    if (depth > 0)
                                    {
                                        holeText.Append(Current);
                                        _position++;
                                    }
                                    else
                                    {
            _position++; // 消费洞闭合 '}'
                                    }
                                }
                                else if (Current == '"')
                                {
            // 跳过洞内字符串（含 "" 转义）
                                    holeText.Append(Current);
                                    _position++;
                                    while (Current != '\0' && !(!verbatim && (Current == '\r' || Current == '\n')))
                                    {
                                        if (Current == '"')
                                        {
                                            holeText.Append(Current);
                                            _position++;
                                            if (Current != '"')
                                            {
                                                break;
                                            }
                                        }
                                        else
                                        {
                                            holeText.Append(Current);
                                            _position++;
                                        }
                                    }
                                }
                                else if (Current == '\'')
                                {
                                    // 跳过洞内 char 字面量（1b/B4）：旧实现只有 '{'/'}'/'"' 三分支，
                                    // `$"{ '}' }"` 的 char 内 '}' 会被当洞闭合符，洞被提前截断
                                    holeText.Append(Current);
                                    _position++;
                                    while (Current != '\0' && Current != '\'' && !(!verbatim && (Current == '\r' || Current == '\n')))
                                    {
                                        if (Current == '\\' && !verbatim)
                                        {
                                            // 转义（'\'' / '\\'）：连同被转义字符一并跳过
                                            holeText.Append(Current);
                                            _position++;
                                            if (Current == '\0')
                                            {
                                                break;
                                            }
                                        }

                                        holeText.Append(Current);
                                        _position++;
                                    }

                                    if (Current == '\'')
                                    {
                                        holeText.Append(Current);
                                        _position++;
                                    }
                                }
                                else if (Current == '/' && Lookahead == '/')
                                {
                                    // 跳过洞内行注释（1b/B4）：`// }` 注释里的 '}' 不再闭合洞
                                    while (Current != '\0' && Current != '\r' && Current != '\n')
                                    {
                                        holeText.Append(Current);
                                        _position++;
                                    }
                                }
                                else
                                {
                                    holeText.Append(Current);
                                    _position++;
                                }
                            }

                            if (depth > 0)
                            {
                                var span = new TextSpan(_start, 1);
                                var location = new TextLocation(_text, span);
                                _diagnostics.ReportUnterminatedString(location);
                            }

                            parts.Add(new InterpolatedStringPart(InterpolatedStringPartKind.Hole, holeText.ToString(), holeStart, _position));
                            literalStart = _position;
                        }
                        break;
                    }
                    case '}':
                    {
                        if (Lookahead == '}')
                        {
                            literal.Append('}');
                            _position += 2;
                        }
                        else
                        {
                            literal.Append(Current);
                            _position++;
                        }
                        break;
                    }
                    case '\\':
                    {
                        if (!verbatim)
                        {
                            ReadEscape(literal);
                        }
                        else
                        {
                            literal.Append(Current);
                            _position++;
                        }
                        break;
                    }
                    default:
                    {
                        literal.Append(Current);
                        _position++;
                        break;
                    }
                }
            }

            if (literal.Length > 0)
            {
                parts.Add(new InterpolatedStringPart(InterpolatedStringPartKind.Literal, literal.ToString(), literalStart, _position));
            }

            _kind = SyntaxKind.InterpolatedStringToken;
            _value = parts.ToArray();
        }

    }
}

