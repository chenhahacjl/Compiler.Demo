using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 词法分析器 (Lexical Analyzer)（S-2 Lexer 分家：C# 专属词法逻辑随语言库落位）
    /// <br/>
    /// 字符 => Token
    /// </summary>
    internal sealed partial class CSharpLexer : ILexer
    {
        private readonly DiagnosticBag _diagnostics = new DiagnosticBag();
        private readonly SyntaxTree _syntaxTree;
        private readonly SourceText _text;

        private int _position;

        private int _start;
        private SyntaxKind _kind;
        private object? _value;
        private ImmutableArray<SyntaxTrivia>.Builder _triviaBuilder = ImmutableArray.CreateBuilder<SyntaxTrivia>();

        public CSharpLexer(SyntaxTree syntaxTree)
        {
            _syntaxTree = syntaxTree;
            _text = syntaxTree.Text;
        }

        /// <summary>从指定位置开始词法（用于插值洞的子解析，位置必须指向洞首字符）。</summary>
        public CSharpLexer(SyntaxTree syntaxTree, int start)
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

    }
}
