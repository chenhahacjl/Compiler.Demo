using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 璇嶆硶鍒嗘瀽鍣?(Lexical Analyzer)
    /// <br/>
    /// 瀛楃 => Token
    /// </summary>
    internal abstract partial class LexerBase : ILexer
    {
        private void ReadString()
        {
            // "Test \" String"
            // "Test "" String"
            // "Test \n String"

            // 璺宠繃褰撳墠寮曞彿
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
        /// verbatim 瀛楃涓?<c>@"..."</c>锛氫笉澶勭悊 <c>\</c> 杞箟锛?c>""</c> 杞箟寮曞彿锛涘厑璁稿琛岋紙鍘熸牱淇濈暀锛夈€?
        /// </summary>
        private void ReadVerbatimString()
        {
            // 褰撳墠浣嶇疆鍦?'@'锛氳烦杩?'@'
            _position++;
            // 璺宠繃寮€澶村紩鍙?
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
                        // 澶氳锛歕r/\n 鍘熸牱淇濈暀
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
        /// raw 瀛楃涓?<c>"""..."""</c>锛氬畾鐣岀 = 璧峰鏈€澶у紩鍙蜂覆 N锛堚墺3锛夛紝鍐呭鐩村埌鍚岄暱 N 寮曞彿涓查棴鍚堬紱
        /// 涓嶅鐞嗚浆涔夛紱澶氳鎸夐棴鍚堝畾鐣岀鎵€鍦ㄥ垪鍓ョ姣忚鍓嶅绌虹櫧銆傛敞锛氬叏寮曞彿绌轰覆 <c>""""""</c>锛圕# 鍚堟硶锛夋澶勬姤鏈粓姝€?
        /// </summary>
        private void ReadRawString()
        {
            // 褰撳墠浣嶇疆鍦?'"'锛氱粺璁¤捣濮嬪紩鍙蜂覆
            var delimiterStart = _position;
            var delimiter = 0;
            while (Current == '"')
            {
                delimiter++;
                _position++;
            }

            if (delimiter < 3)
            {
                // 鐢卞垎娲句繚璇佷笉浼氳蛋鍒帮紝闃插尽鎬у洖閫€涓烘櫘閫氬瓧绗︿覆
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
                    // 缁熻寮曞彿涓?
                    var runStart = _position;
                    var run = 0;
                    while (Current == '"')
                    {
                        run++;
                        _position++;
                    }

                    if (run >= delimiter)
                    {
                        // 鍚岄暱 N 闂悎锛堟洿闀跨殑寮曞彿涓蹭互 N 缁撳熬涔熼棴鍚堬紝澶氫綑閮ㄥ垎瑙嗕负鍐呭鍓嶅紩鍙封€斺€旇椽蹇冭鍒欏彇棣栨 N 杩炲紩锛?
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

            // 澶氳缂╄繘鍓ョ锛堜粎鍐呭璺ㄨ鏃讹級锛氶棴鍚堝畾鐣岀鎵€鍦ㄥ垪 = 姣忚鍓ョ鐨勫墠瀵肩┖鐧芥暟
            if (_text.GetLineIndex(contentStart) < _text.GetLineIndex(_position))
            {
                // C# 11锛氬紑瀹氱晫绗﹀悗绱ц窡鐨勬崲琛屼笉璁″叆鍐呭
                if (stringBuilder.Length > 0 && (stringBuilder[0] == '\r' || stringBuilder[0] == '\n'))
                {
                    var remove = stringBuilder[0] == '\r' && stringBuilder.Length > 1 && stringBuilder[1] == '\n' ? 2 : 1;
                    stringBuilder.Remove(0, remove);
                }

                var closingLineIndex = _text.GetLineIndex(_position);
                var closingLine = _text.Lines[closingLineIndex];
                var indent = _position - closingLine.Start; // 闂悎瀹氱晫绗﹀湪璇ヨ鐨勫垪
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

                // C# 11锛氶棴鍚堝畾鐣岀鎵€鍦ㄨ涔嬪墠缁撳熬鐨勬崲琛屼笉璁″叆鍐呭
                if (stringBuilder.Length > 0 && stringBuilder[^1] == '\n')
                {
                    var remove = stringBuilder.Length > 1 && stringBuilder[^2] == '\r' ? 2 : 1;
                    stringBuilder.Remove(stringBuilder.Length - remove, remove);
                }
            }

            _kind = SyntaxKind.RawStringToken;
            _value = stringBuilder.ToString();
        }

        /// <summary>澶勭悊涓€涓浆涔夊簭鍒楋紙褰撳墠浣嶇疆鍦?<c>\</c>锛夛紝杩藉姞缁撴灉鍒?builder 骞舵帹杩涗綅缃€?/summary>
        private void ReadEscape(StringBuilder stringBuilder)
        {
            var escapeStart = _position;
            _position++; // 娑堣垂 '\'
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
        /// 鎻掑€煎瓧绗︿覆 <c>$"..."</c> / <c>$@"..."</c> / <c>@$"..."</c>锛氬垏鍒嗕负瀛楅潰閲忔枃鏈涓庢礊锛?c>{expr}</c>锛夈€?
        /// 娲炴惡甯︽簮鏂囨湰涓庣粷瀵?Span锛堝惈娲炲唴瀛楃涓蹭腑鐨?<c>{</c>/<c>}</c> 璺宠繃锛夛紝渚?Parser 閫愭礊瀛愯В鏋愬苟淇濊瘉璇婃柇瀹氫綅銆?
        /// verbatim 妯″紡锛堝惈 <c>@</c> 鍓嶇紑锛夛細瀛楅潰閲?娲炲厑璁告崲琛屽師鏍蜂繚鐣欍€佷笉澶勭悊 <c>\</c> 杞箟锛?
        /// 鏅€氭ā寮忥細鍗曡銆佸瓧闈㈤噺娈靛鐞?<c>\</c> 杞箟銆?
        /// </summary>
        private void ReadInterpolatedString(bool verbatim)
        {
            var parts = new List<InterpolatedStringPart>();
            var literal = new StringBuilder();

            // 娑堣垂鍓嶇紑锛? 鎴?@锛屽啀閰嶅鍓嶇紑锛?@ / @$锛夛紝鍐嶅紑澶村紩鍙?
            _position++; // 娑堣垂 '$' 鎴?'@'
            if (Current == '@' || Current == '$')
            {
                _position++;
            }
            _position++; // 娑堣垂寮€澶村紩鍙?

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
                            // 鍐插埛瀛楅潰閲忔
                            if (literal.Length > 0)
                            {
                                parts.Add(new InterpolatedStringPart(InterpolatedStringPartKind.Literal, literal.ToString(), literalStart, _position));
                                literal.Clear();
                            }

                            // 鎵弿娲炲埌鍖归厤 '}'锛堣烦杩囨礊鍐呭瓧绗︿覆涓殑 '}'/'{'锛泇erbatim 鏀捐鎹㈣锛?
                            var holeStart = _position + 1;
                            _position++; // 璺宠繃 '{'
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
                                        _position++; // 娑堣垂娲為棴鍚?'}'
                                    }
                                }
                                else if (Current == '"')
                                {
                                    // 璺宠繃娲炲唴瀛楃涓诧紙鍚?"" 杞箟锛?
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

