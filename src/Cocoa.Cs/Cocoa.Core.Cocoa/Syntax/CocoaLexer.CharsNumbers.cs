using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 璇嶆硶鍒嗘瀽鍣?(Lexical Analyzer)
    /// <br/>
    /// 瀛楃 => Token
    /// </summary>
    internal sealed partial class CocoaLexer : ILexer
    {
        private void ReadChar()
        {
            // 璺宠繃褰撳墠寮曞彿
            _position++;

            var value = '\0';
            var hasError = false;

            if (Current == '\'' || Current == '\0' || Current == '\r' || Current == '\n')
            {
                hasError = true;
            }
            else if (Current == '\\')
            {
                _position++;
                switch (Current)
                {
                    case 'n': value = '\n'; _position++; break;
                    case 't': value = '\t'; _position++; break;
                    case 'r': value = '\r'; _position++; break;
                    case '0': value = '\0'; _position++; break;
                    case '\\': value = '\\'; _position++; break;
                    case '\'': value = '\''; _position++; break;
                    case '"': value = '"'; _position++; break;
                    case 'u':
                    case 'x':
                        {
                            var isFixed = Current == 'u'; // \u 鍥哄畾 4 浣嶏紱\x 鍙彉 1~4 浣?
                            var required = isFixed ? 4 : 0;
                            _position++;
                            var digits = 0;
                            while ((required == 0 ? digits < 4 : digits < required) && IsHexDigit(Current))
                            {
                                digits++;
                                _position++;
                            }

                            if (digits == 0 || (required > 0 && digits != required))
                            {
                                hasError = true;
                            }
                            else
                            {
                                var hex = _text.ToString(_position - digits, digits);
                                value = (char)Convert.ToInt32(hex, 16);
                            }
                            break;
                        }
                    case 'U':
                        {
                            _position++;
                            if (!IsHexDigit(Current) || !IsHexDigit(Peek(1)) || !IsHexDigit(Peek(2)) || !IsHexDigit(Peek(3)) ||
                                !IsHexDigit(Peek(4)) || !IsHexDigit(Peek(5)) || !IsHexDigit(Peek(6)) || !IsHexDigit(Peek(7)))
                            {
                                hasError = true;
                            }
                            else
                            {
                                var codePoint = Convert.ToInt32(_text.ToString(_position, 8), 16);
                                if (codePoint > 0xFFFF)
                                {
                                    hasError = true; // char 瀹逛笉涓嬩唬鐞嗗
                                }
                                else
                                {
                                    value = (char)codePoint;
                                    _position += 8;
                                }
                            }
                            break;
                        }
                    default:
                        hasError = true;
                        break;
                }
            }
            else
            {
                value = Current;
                _position++;
            }

            if (hasError || Current != '\'')
            {
                var span = new TextSpan(_start, 1);
                var location = new TextLocation(_text, span);
                _diagnostics.ReportBadCharacter(location, Current);
                _kind = SyntaxKind.BadToken;
                return;
            }

            // 璺宠繃鏀跺熬寮曞彿
            _position++;
            _kind = SyntaxKind.CharToken;
            _value = value;
        }

        private void ReadNumber()
        {
            var length = 0;
            var isHex = false;
            var hasExponent = false;

            if (Current == '0' && Peek(1) == 'x')
            {
                isHex = true;
                _position += 2;
                length = 2;

                while (IsHexDigit(Current))
                {
                    _position++;
                    length++;
                }
            }
            else
            {
                while (char.IsDigit(Current))
                {
                    _position++;
                    length++;
                }

                if (Current == '.' && char.IsDigit(Peek(1)))
                {
                    _position++;
                    length++;
                    while (char.IsDigit(Current))
                    {
                        _position++;
                        length++;
                    }
                }

                if (Current == 'e' || Current == 'E')
                {
                    var hasSign = Peek(1) == '+' || Peek(1) == '-';
                    var exponentDigit = hasSign ? Peek(2) : Peek(1);
                    if (char.IsDigit(exponentDigit))
                    {
                        _position++;
                        length++;
                        if (hasSign)
                        {
                            _position++;
                            length++;
                        }

                        while (char.IsDigit(Current))
                        {
                            _position++;
                            length++;
                        }

                        hasExponent = true;
                    }
                }
            }

            // 绫诲瀷鍚庣紑锛?e-M21锛夛細`42L`/`0xFFL`銆乣1u`/`1U`銆乣1ul`/`1UL`/`1lu`/`1LU`銆乣1.0f`/`1e5f`銆?
            // 浠呭綋鍚庣紑鍚庨潪鏍囪瘑绗﹀瓧绗︽椂鐢熸晥锛岄伩鍏嶅悶鎺?let / long / ulong / using 绛夊叧閿瓧/鏍囪瘑绗?
            // 锛堝 `9696let` 搴旀媶涓?9696 + let锛宍1234long` 搴旀媶涓?1234 + long锛宍1ul` 涓嶅簲鎷嗘垚 `1`+`ul`锛夈€?
            // 鍙屽瓧姣嶇粍鍚堬紙ul/UL/lu/LU锛夋寜 Peek(2) 鍒ゅ畾杈圭晫銆?
            bool uSuffix = false, lSuffix = false, fSuffix = false;
            {
                var s = Current;
                if (s == 'u' || s == 'U')
                {
                    if ((Peek(1) == 'l' || Peek(1) == 'L') && !char.IsLetterOrDigit(Peek(2)))
                    {
                        uSuffix = true;
                        lSuffix = true;
                        _position += 2;
                        length += 2;
                    }
                    else if (!char.IsLetterOrDigit(Peek(1)))
                    {
                        uSuffix = true;
                        _position++;
                        length++;
                    }
                }
                else if (s == 'l' || s == 'L')
                {
                    if ((Peek(1) == 'u' || Peek(1) == 'U') && !char.IsLetterOrDigit(Peek(2)))
                    {
                        lSuffix = true;
                        uSuffix = true;
                        _position += 2;
                        length += 2;
                    }
                    else if (!char.IsLetterOrDigit(Peek(1)))
                    {
                        lSuffix = true;
                        _position++;
                        length++;
                    }
                }
                else if ((s == 'f' || s == 'F') && !char.IsLetterOrDigit(Peek(1)))
                {
                    fSuffix = true;
                    _position++;
                    length++;
                }
            }

            var text = _text.ToString(_start, length);

            if (fSuffix)
            {
                var floatText = isHex ? text.Substring(2, text.Length - 3) : text.Substring(0, text.Length - 1);
                var floatStyle = isHex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Float;
                if (double.TryParse(floatText, floatStyle, null, out var floatDouble))
                {
                    _value = (float)floatDouble;
                    _kind = SyntaxKind.DoubleToken;
                    return;
                }

                var span = new TextSpan(_start, length);
                var location = new TextLocation(_text, span);
                _diagnostics.ReportInvalidNumber(location, text, TypeSymbol.Float);
                _value = 0.0f;
                _kind = SyntaxKind.DoubleToken;
                return;
            }

            if (!isHex && (text.Contains('.') || hasExponent))
            {
                var doubleValue = 0.0;
                if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out doubleValue))
                {
                    var span = new TextSpan(_start, length);
                    var location = new TextLocation(_text, span);
                    _diagnostics.ReportInvalidNumber(location, text, TypeSymbol.Double);
                }

                _value = doubleValue;
                _kind = SyntaxKind.DoubleToken;
                return;
            }

            if (lSuffix)
            {
                if (uSuffix)
                {
                    var ulongText = isHex ? text.Substring(2, text.Length - 4) : text.Substring(0, text.Length - 2);
                    if (ulong.TryParse(ulongText, isHex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer, null, out var ulongValue))
                    {
                        _value = ulongValue;
                        _kind = SyntaxKind.NumberToken;
                        return;
                    }

                    var span = new TextSpan(_start, length);
                    var location = new TextLocation(_text, span);
                    _diagnostics.ReportInvalidNumber(location, text, TypeSymbol.UInt64);
                    _value = 0UL;
                    _kind = SyntaxKind.NumberToken;
                    return;
                }

                var longText = isHex ? text.Substring(2, text.Length - 3) : text.Substring(0, text.Length - 1);
                if (long.TryParse(longText, isHex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer, null, out var longValue))
                {
                    _value = longValue;
                    _kind = SyntaxKind.NumberToken;
                    return;
                }

                var longSpan = new TextSpan(_start, length);
                var longLocation = new TextLocation(_text, longSpan);
                _diagnostics.ReportInvalidNumber(longLocation, text, TypeSymbol.Int64);
                _value = 0L;
                _kind = SyntaxKind.NumberToken;
                return;
            }

            if (uSuffix)
            {
                var uintText = isHex ? text.Substring(2, text.Length - 3) : text.Substring(0, text.Length - 1);
                if (uint.TryParse(uintText, isHex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer, null, out var uintValue))
                {
                    _value = uintValue;
                    _kind = SyntaxKind.NumberToken;
                    return;
                }

                var span = new TextSpan(_start, length);
                var location = new TextLocation(_text, span);
                _diagnostics.ReportInvalidNumber(location, text, TypeSymbol.UInt32);
                _value = 0U;
                _kind = SyntaxKind.NumberToken;
                return;
            }

            var value = 0;
            var parsed = isHex
                ? int.TryParse(text.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out value)
                : int.TryParse(text, out value);

            if (!parsed)
            {
                // >int.MaxValue 鐨勬暣鏁板瓧闈㈤噺鑷姩鍗囨牸涓?long锛圕# 鍚屾瀯锛氬崄杩涘埗澶ф暣鏁板彇鏈€灏忓彲瀹圭撼绫诲瀷锛?
                var bigText = isHex ? text.Substring(2) : text;
                if (long.TryParse(bigText, isHex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer, null, out var upgraded))
                {
                    _value = upgraded;
                    _kind = SyntaxKind.NumberToken;
                    return;
                }

                var span = new TextSpan(_start, length);
                var location = new TextLocation(_text, span);
                _diagnostics.ReportInvalidNumber(location, text, TypeSymbol.Int32);
            }

            _value = value;
            _kind = SyntaxKind.NumberToken;
        }

        private static bool IsHexDigit(char c)
        {
            return char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        private void ReadIdentifierOrKeyword()
        {
            while (char.IsLetterOrDigit(Current) || Current == '_')
            {
                _position++;
            }

            var length = _position - _start;
            var text = _text.ToString(_start, length);

            // P1-A 璇嶆硶鍒嗗锛氬叧閿瓧璇嗗埆缁忚瑷€涓撳睘琛紙CO 琛?= 鍏变韩鍏ㄨ〃锛汣# 琛ㄥ湪 P1-A(ii) 鎺掗櫎 CO 鐙崰璇嶏級
            _kind = _syntaxTree.Language.GetKeywordKind(text);
        }
    }
}

