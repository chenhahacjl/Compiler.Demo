using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 词法分析器 (Lexical Analyzer)
    /// <br/>
    /// 字符 => Token
    /// </summary>
    internal sealed class Lexer
    {
        private readonly DiagnosticBag _diagnostics = new DiagnosticBag();
        private readonly SyntaxTree _syntaxTree;
        private readonly SourceText _text;

        private int _position;

        private int _start;
        private SyntaxKind _kind;
        private object? _value;
        private ImmutableArray<SyntaxTrivia>.Builder _triviaBuilder = ImmutableArray.CreateBuilder<SyntaxTrivia>();

        public Lexer(SyntaxTree syntaxTree)
        {
            _syntaxTree = syntaxTree;
            _text = syntaxTree.Text;
        }

        /// <summary>从指定位置开始词法（用于插值洞的子解析，位置必须指向洞首字符）。</summary>
        public Lexer(SyntaxTree syntaxTree, int start)
            : this(syntaxTree)
        {
            _position = start;
        }

        public DiagnosticBag Diagnostics => _diagnostics;

        private char Current => Peek(0);

        private char Lookahead => Peek(1);

        private char Peek(int offset)
        {
            var index = _position + offset;

            if (index >= _text.Length)
            {
                return '\0';
            }

            return _text[index];
        }

        public SyntaxToken Lex()
        {
            ReadTrivia(leading: true);

            var leadingTrivia = _triviaBuilder.ToImmutable();

            var tokenStart = _position;

            ReadToken();

            var tokenKind = _kind;
            var tokenValue = _value;
            var tokenLength = _position - _start;

            ReadTrivia(leading: false);

            var trailingTrivia = _triviaBuilder.ToImmutable();

            var tokenText = SyntaxFacts.GetText(tokenKind);
            if (tokenText == null)
            {
                tokenText = _text.ToString(tokenStart, tokenLength);
            }

            return new SyntaxToken(_syntaxTree, tokenKind, tokenStart, tokenText, tokenValue, leadingTrivia, trailingTrivia);
        }

        private void ReadTrivia(bool leading)
        {
            _triviaBuilder.Clear();

            var done = false;

            while (!done)
            {
                _start = _position;
                _kind = SyntaxKind.BadToken;
                _value = null;

                switch (Current)
                {
                    case '\0':
                    {
                        done = true;
                        break;
                    }
                    case '/':
                    {
                        if (Lookahead == '/')
                        {
                            ReadSingleLineComment();
                        }
                        else if (Lookahead == '*')
                        {
                            ReadMultiLineComment();
                        }
                        else
                        {
                            done = true;
                        }

                        break;
                    }
                    case '\r':
                    case '\n':
                    {
                        if (!leading)
                        {
                            done = true;
                        }

                        ReadLineBreak();
                        break;
                    }
                    case ' ':
                    case '\t':
                    {
                        ReadWhiteSpace();
                        break;
                    }
                    default:
                    {
                        if (char.IsWhiteSpace(Current))
                        {
                            ReadWhiteSpace();
                        }
                        else
                        {
                            done = true;
                        }

                        break;
                    }

                }

                var length = _position - _start;

                if (length > 0)
                {
                    var text = _text.ToString(_start, length);
                    var trivia = new SyntaxTrivia(_syntaxTree, _kind, _start, text);

                    _triviaBuilder.Add(trivia);
                }
            }
        }

        private void ReadLineBreak()
        {
            if (Current == '\r' && Lookahead == '\n')
            {
                _position += 2;
            }
            else
            {
                _position++;
            }

            _kind = SyntaxKind.LineBreakTrivia;
        }

        private void ReadWhiteSpace()
        {
            var done = false;

            while (!done)
            {
                switch (Current)
                {
                    case '\0':
                    case '\r':
                    case '\n':
                    {
                        done = true;
                        break;
                    }
                    default:
                    {
                        if (!char.IsWhiteSpace(Current))
                        {
                            done = true;
                        }
                        else
                        {
                            _position++;
                        }

                        break;
                    }
                }
            }

            _kind = SyntaxKind.WhitespaceTrivia;
        }

        private void ReadSingleLineComment()
        {
            _position += 2;

            var done = false;

            while (!done)
            {
                switch (Current)
                {
                    case '\0':
                    case '\r':
                    case '\n':
                    {
                        done = true;
                        break;
                    }
                    default:
                    {
                        _position++;
                        break;
                    }
                }
            }

            _kind = SyntaxKind.SingleLineCommentTrivia;
        }

        private void ReadMultiLineComment()
        {
            _position += 2;

            var done = false;

            while (!done)
            {
                switch (Current)
                {
                    case '\0':
                    {
                        var span = new TextSpan(_start, 2);
                        var location = new TextLocation(_text, span);
                        _diagnostics.ReportUnterminatedMultiLineComment(location);
                        done = true;

                        break;
                    }
                    case '*':
                    {
                        if (Lookahead == '/')
                        {
                            _position++;
                            done = true;
                        }

                        _position++;
                        break;
                    }
                    default:
                    {
                        _position++;
                        break;
                    }
                }
            }

            _kind = SyntaxKind.MultiLineCommentTrivia;
        }

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
                        // """ 开头 → raw 字符串
                        ReadRawString();
                    }
                    else
                    {
                        ReadString();
                    }
                    break;
                case '$':
                    // $"..."（普通插值）/ $@"..."（verbatim 插值）
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
                    // @"..."（verbatim 字符串）/ @$"..."（verbatim 插值）/ @ident（verbatim 标识符）
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
                        _position++; // 消费 '@'，_start 保持 @ 位置 → token 文本含 @（名含 @）
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
        /// verbatim 字符串 <c>@"..."</c>：不处理 <c>\</c> 转义；<c>""</c> 转义引号；允许多行（原样保留）。
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
                        // 同长 N 闭合（更长的引号串以 N 结尾也闭合，多余部分视为内容前引号——贪心规则取首次 N 连引）
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
        /// 洞携带源文本与绝对 Span（含洞内字符串中的 <c>{</c>/<c>}</c> 跳过），供 Parser 逐洞子解析并保证诊断定位。
        /// verbatim 模式（含 <c>@</c> 前缀）：字面量/洞允许换行原样保留、不处理 <c>\</c> 转义；
        /// 普通模式：单行、字面量段处理 <c>\</c> 转义。
        /// </summary>
        private void ReadInterpolatedString(bool verbatim)
        {
            var parts = new List<InterpolatedStringPart>();
            var literal = new StringBuilder();

            // 消费前缀：$ 或 @，再配对前缀（$@ / @$），再开头引号
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

        private void ReadChar()
        {
            // 跳过当前引号
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
                            var isFixed = Current == 'u'; // \u 固定 4 位；\x 可变 1~4 位
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
                                    hasError = true; // char 容不下代理对
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

            // 跳过收尾引号
            _position++;
            _kind = SyntaxKind.CharToken;
            _value = value;
        }

        private void ReadNumber()
        {
            var length = 0;
            var isHex = false;
            var hasExponent = false;
            var hasLongSuffix = false;

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

            // long 后缀（6e-M19 M1）：`42L` / `0xFFL`（大小写均可）。
            // 仅当 L 后非标识符字符时生效，避免吞掉 let / long 等关键字
            // （如 `9696let` 应拆为 9696 + let，`1234long` 应拆为 1234 + long）。
            bool canTakeLongSuffix = !char.IsLetterOrDigit(Peek(1));
            if (!isHex && !hasExponent && (Current == 'L' || Current == 'l') && canTakeLongSuffix)
            {
                hasLongSuffix = true;
                _position++;
                length++;
            }
            else if (isHex && (Current == 'L' || Current == 'l') && canTakeLongSuffix)
            {
                hasLongSuffix = true;
                _position++;
                length++;
            }

            var text = _text.ToString(_start, length);

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

            if (hasLongSuffix)
            {
                var longText = isHex ? text.Substring(2, text.Length - 3) : text.Substring(0, text.Length - 1);
                var longParsed = long.TryParse(longText, isHex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer, null, out var longValue);
                if (longParsed)
                {
                    _value = longValue;
                    _kind = SyntaxKind.NumberToken;
                    return;
                }

                var longSpan = new TextSpan(_start, length);
                var longLocation = new TextLocation(_text, longSpan);
                _diagnostics.ReportInvalidNumber(longLocation, text, TypeSymbol.Long);
                _value = 0L;
                _kind = SyntaxKind.NumberToken;
                return;
            }

            var value = 0;
            var parsed = isHex
                ? int.TryParse(text.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out value)
                : int.TryParse(text, out value);

            if (!parsed)
            {
                // >int.MaxValue 的整数字面量自动升格为 long（C# 同构：十进制大整数取最小可容纳类型）
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

            _kind = SyntaxFacts.GetKeywordKind(text);
        }
    }
}
