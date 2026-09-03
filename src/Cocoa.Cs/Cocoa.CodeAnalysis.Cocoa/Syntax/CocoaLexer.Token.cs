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
        private void ReadToken()
        {
            _start = _position;
            _kind = SyntaxKind.BadToken;
            _value = null;

            switch (Current)
            {
                case '\0':
                {
                    _kind = SyntaxKind.EndOfFileToken;
                    break;
                }
                case '+':
                {
                    _position++;
                    if (Current == '+')
                    {
                        _kind = SyntaxKind.PlusPlusToken;
                        _position++;
                    }
                    else if (Current != '=')
                    {
                        _kind = SyntaxKind.PlusToken;
                    }
                    else
                    {
                        _kind = SyntaxKind.PlusEqualsToken;
                        _position++;
                    }
                    break;
                }
                case '-':
                {
                    _position++;
                    if (Current == '-')
                    {
                        _kind = SyntaxKind.MinusMinusToken;
                        _position++;
                    }
                    else if (Current == '>')
                    {
                        // 鍑芥暟绫诲瀷绠ご锛?e-M22 C2锛夛細`(int) -> int`锛?cs 鏂硅█鍦ㄨВ鏋愬眰鎷掔粷
                        _position++;
                        _kind = SyntaxKind.ArrowToken;
                    }
                    else if (Current != '=')
                    {
                        _kind = SyntaxKind.MinusToken;
                    }
                    else
                    {
                        _kind = SyntaxKind.MinusEqualsToken;
                        _position++;
                    }
                    break;
                }
                case '*':
                {
                    _position++;
                    if (Current != '=')
                    {
                        _kind = SyntaxKind.StarToken;
                    }
                    else
                    {
                        _kind = SyntaxKind.StarEqualsToken;
                        _position++;
                    }
                    break;
                }
                case '/':
                {
                    _position++;
                    if (Current != '=')
                    {
                        _kind = SyntaxKind.SlashToken;
                    }
                    else
                    {
                        _kind = SyntaxKind.SlashEqualsToken;
                        _position++;
                    }
                    break;
                }
                case '(':
                {
                    _kind = SyntaxKind.OpenParenthesisToken;
                    _position++;
                    break;
                }
                case ')':
                {
                    _kind = SyntaxKind.CloseParenthesisToken;
                    _position++;
                    break;
                }
                case '[':
                {
                    _kind = SyntaxKind.OpenBracketToken;
                    _position++;
                    break;
                }
                case ']':
                {
                    _kind = SyntaxKind.CloseBracketToken;
                    _position++;
                    break;
                }
                case '{':
                {
                    _kind = SyntaxKind.OpenBraceToken;
                    _position++;
                    break;
                }
                case '}':
                {
                    _kind = SyntaxKind.CloseBraceToken;
                    _position++;
                    break;
                }
                case ':':
                {
                    _kind = SyntaxKind.ColonToken;
                    _position++;
                    break;
                }
                case ',':
                {
                    _kind = SyntaxKind.CommaToken;
                    _position++;
                    break;
                }
                case '.':
                {
                    _kind = SyntaxKind.DotToken;
                    _position++;
                    break;
                }
                case ';':
                {
                    _kind = SyntaxKind.SemicolonToken;
                    _position++;
                    break;
                }
                case '~':
                {
                    _kind = SyntaxKind.TildeToken;
                    _position++;
                    break;
                }
                case '^':
                {
                    _position++;
                    if (Current != '=')
                    {
                        _kind = SyntaxKind.HatToken;
                    }
                    else
                    {
                        _kind = SyntaxKind.HatEqualsToken;
                        _position++;
                    }
                    break;
                }
                case '&':
                {
                    _position++;
                    if (Current == '&')
                    {
                        _kind = SyntaxKind.AmpersandAmpersandToken;
                        _position++;
                    }
                    else if (Current == '=')
                    {
                        _kind = SyntaxKind.AmpersandEqualsToken;
                        _position++;
                    }
                    else
                    {
                        _kind = SyntaxKind.AmpersandToken;
                    }
                    break;
                }
                case '|':
                {
                    _position++;
                    if (Current == '|')
                    {
                        _kind = SyntaxKind.PipePipeToken;
                        _position++;
                    }
                    else if (Current == '=')
                    {
                        _kind = SyntaxKind.PipeEqualsToken;
                        _position++;
                    }
                    else
                    {
                        _kind = SyntaxKind.PipeToken;
                    }
                    break;
                }
                case '=':
                {
                    _position++;
                    if (Current == '>')
                    {
                        _position++;
                        _kind = SyntaxKind.FatArrowToken;
                    }
                    else if (Current != '=')
                    {
                        _kind = SyntaxKind.EqualsToken;
                    }
                    else
                    {

                        _position++;
                        _kind = SyntaxKind.EqualsEqualsToken;
                    }
                    break;
                }
                case '!':
                {
                    _position++;
                    if (Current != '=')
                    {
                        _kind = SyntaxKind.BangToken;
                    }
                    else
                    {
                        _kind = SyntaxKind.BangEqualsToken;
                        _position++;
                    }
                    break;
                }
                case '<':
                {
                    _position++;
                    if (Current == '<')
                    {
                        _position++;
                        if (Current == '=')
                        {
                            _kind = SyntaxKind.ShiftLeftEqualsToken;
                            _position++;
                        }
                        else
                        {
                            _kind = SyntaxKind.ShiftLeftToken;
                        }
                    }
                    else if (Current == '=')
                    {
                        _kind = SyntaxKind.LessOrEqualsToken;
                        _position++;
                    }
                    else
                    {
                        _kind = SyntaxKind.LessToken;
                    }
                    break;
                }
                case '>':
                {
                    _position++;
                    if (Current == '>')
                    {
                        _position++;
                        if (Current == '=')
                        {
                            _kind = SyntaxKind.ShiftRightEqualsToken;
                            _position++;
                        }
                        else
                        {
                            _kind = SyntaxKind.ShiftRightToken;
                        }
                    }
                    else if (Current == '=')
                    {
                        _kind = SyntaxKind.GreaterOrEqualsToken;
                        _position++;
                    }
                    else
                    {
                        _kind = SyntaxKind.GreaterToken;
                    }
                    break;
                }
                case '%':
                {
                    _position++;
                    if (Current == '=')
                    {
                        _kind = SyntaxKind.PercentEqualsToken;
                        _position++;
                    }
                    else
                    {
                        _kind = SyntaxKind.PercentToken;
                    }
                    break;
                }
                case '?':
                {
                    _kind = SyntaxKind.QuestionToken;
                    _position++;
                    break;
                }
                case '"':
                    if (Lookahead == '"' && Peek(2) == '"')
                    {
                        // """ 寮€澶?鈫?raw 瀛楃涓?
                        ReadRawString();
                    }
                    else
                    {
                        ReadString();
                    }
                    break;
                case '$':
                    // $"..."锛堟櫘閫氭彃鍊硷級/ $@"..."锛坴erbatim 鎻掑€硷級
                    if (Lookahead == '"')
                    {
                        ReadInterpolatedString(verbatim: false);
                    }
                    else if (Lookahead == '@' && Peek(2) == '"')
                    {
                        ReadInterpolatedString(verbatim: true);
                    }
                    else
                    {
                        var span = new TextSpan(_position, 1);
                        var location = new TextLocation(_text, span);
                        _diagnostics.ReportBadCharacter(location, Current);
                        _position++;
                    }
                    break;
                case '@':
                    // @"..."锛坴erbatim 瀛楃涓诧級/ @$"..."锛坴erbatim 鎻掑€硷級/ @ident锛坴erbatim 鏍囪瘑绗︼級
                    if (Lookahead == '"')
                    {
                        ReadVerbatimString();
                    }
                    else if (Lookahead == '$' && Peek(2) == '"')
                    {
                        ReadInterpolatedString(verbatim: true);
                    }
                    else if (char.IsLetter(Peek(1)) || Peek(1) == '_')
                    {
                        _position++; // 娑堣垂 '@'锛宊start 淇濇寔 @ 浣嶇疆 鈫?token 鏂囨湰鍚?@锛堝悕鍚?@锛?
                        ReadIdentifierOrKeyword();
                    }
                    else
                    {
                        var span = new TextSpan(_position, 1);
                        var location = new TextLocation(_text, span);
                        _diagnostics.ReportBadCharacter(location, Current);
                        _position++;
                    }
                    break;
                case '\'':
                    ReadChar();
                    break;
                case '0':
                case '1':
                case '2':
                case '3':
                case '4':
                case '5':
                case '6':
                case '7':
                case '8':
                case '9':
                {
                    ReadNumber();
                    break;
                }
                case '_':
                {
                    ReadIdentifierOrKeyword();
                    break;
                }
                default:
                {
                    if (char.IsLetter(Current))
                    {
                        ReadIdentifierOrKeyword();
                    }
                    else
                    {
                        var span = new TextSpan(_position, 1);
                        var location = new TextLocation(_text, span);

                        _diagnostics.ReportBadCharacter(location, Current);
                        _position++;
                    }
                    break;
                }
            }
        }

    }
}

