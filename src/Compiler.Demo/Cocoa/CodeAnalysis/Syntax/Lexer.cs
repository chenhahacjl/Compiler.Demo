using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Text;
using System.Collections.Generic;
using System.Text;

namespace Cocoa.CodeAnalysis.Syntax
{
    internal sealed class Lexer
    {
        private readonly DiagnosticBag m_diagnostics = new DiagnosticBag();
        private readonly SourceText m_text;

        private int m_position;

        private int m_start;
        private SyntaxKind m_kind;
        private object m_value;

        public Lexer(SourceText text)
        {
            m_text = text;
        }

        public DiagnosticBag Diagnostics => m_diagnostics;

        private char Current => Peek(0);

        private char Lookahead => Peek(1);

        private char Peek(int offset)
        {
            var index = m_position + offset;

            if (index >= m_text.Length)
            {
                return '\0';
            }

            return m_text[index];
        }

        public SyntaxToken Lex()
        {
            m_start = m_position;
            m_kind = SyntaxKind.BadToken;
            m_value = null;

            switch (Current)
            {
                case '\0':
                {
                    m_kind = SyntaxKind.EndOfFileToken;
                    break;
                }
                case '+':
                {
                    m_kind = SyntaxKind.PlusToken;
                    m_position++;
                    break;
                }
                case '-':
                {
                    m_kind = SyntaxKind.MinusToken;
                    m_position++;
                    break;
                }
                case '*':
                {
                    m_kind = SyntaxKind.StarToken;
                    m_position++;
                    break;
                }
                case '/':
                {
                    m_kind = SyntaxKind.SlashToken;
                    m_position++;
                    break;
                }
                case '(':
                {
                    m_kind = SyntaxKind.OpenParenthesisToken;
                    m_position++;
                    break;
                }
                case ')':
                {
                    m_kind = SyntaxKind.CloseParenthesisToken;
                    m_position++;
                    break;
                }
                case '{':
                {
                    m_kind = SyntaxKind.OpenBraceToken;
                    m_position++;
                    break;
                }
                case '}':
                {
                    m_kind = SyntaxKind.CloseBraceToken;
                    m_position++;
                    break;
                }
                case '~':
                {
                    m_kind = SyntaxKind.TildeToken;
                    m_position++;
                    break;
                }
                case '^':
                {
                    m_kind = SyntaxKind.HatToken;
                    m_position++;
                    break;
                }
                case '&':
                {
                    m_position++;
                    if (Current != '&')
                    {
                        m_kind = SyntaxKind.AmpersandToken;
                    }
                    else
                    {

                        m_position++;
                        m_kind = SyntaxKind.AmpersandAmpersandToken;
                    }
                    break;
                }
                case '|':
                {
                    m_position++;
                    if (Current != '|')
                    {
                        m_kind = SyntaxKind.PipeToken;
                    }
                    else
                    {

                        m_position++;
                        m_kind = SyntaxKind.PipePipeToken;
                    }
                    break;
                }
                case '=':
                {
                    m_position++;
                    if (Current != '=')
                    {
                        m_kind = SyntaxKind.EqualsToken;
                    }
                    else
                    {

                        m_position++;
                        m_kind = SyntaxKind.EqualsEqualsToken;
                    }
                    break;
                }
                case '!':
                {
                    m_position++;
                    if (Current != '=')
                    {
                        m_kind = SyntaxKind.BangToken;
                    }
                    else
                    {
                        m_kind = SyntaxKind.BangEqualsToken;
                        m_position++;
                    }
                    break;
                }
                case '<':
                {
                    m_position++;
                    if (Current != '=')
                    {
                        m_kind = SyntaxKind.LessToken;
                    }
                    else
                    {
                        m_kind = SyntaxKind.LessOrEqualsToken;
                        m_position++;
                    }
                    break;
                }
                case '>':
                {
                    m_position++;
                    if (Current != '=')
                    {
                        m_kind = SyntaxKind.GreaterToken;
                    }
                    else
                    {
                        m_kind = SyntaxKind.GreaterOrEqualsToken;
                        m_position++;
                    }
                    break;
                }
                case '"':
                    ReadString();
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
                case ' ':
                case '\t':
                case '\n':
                case '\r':
                {
                    ReadWhiteSpace();
                    break;
                }
                default:
                {
                    if (char.IsLetter(Current))
                    {
                        ReadIdentifierOrKeyword();
                    }
                    else if (char.IsWhiteSpace(Current))
                    {
                        ReadWhiteSpace();
                    }
                    else
                    {
                        m_diagnostics.ReportBadCharacter(m_position, Current);
                        m_position++;
                    }
                    break;
                }
            }

            var length = m_position - m_start;
            var text = SyntaxFacts.GetText(m_kind);
            if (text == null)
            {
                text = m_text.ToString(m_start, length);
            }

            return new SyntaxToken(m_kind, m_start, text, m_value);
        }

        private void ReadString()
        {
            // "Test \" String"
            // "Test "" String"

            // 跳过当前引号
            m_position++;

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
                        var span = new TextSpan(m_start, 1);
                        m_diagnostics.ReportUnterminatedString(span);
                        done = true;

                        break;
                    }
                    case '"':
                    {
                        if (Lookahead == '"')
                        {
                            stringBuilder.Append(Current);
                            m_position += 2;
                        }
                        else
                        {
                            m_position++;
                            done = true;
                        }

                        break;
                    }
                    default:
                    {
                        stringBuilder.Append(Current);
                        m_position++;

                        break;
                    }
                }
            }

            m_kind = SyntaxKind.StringToken;
            m_value = stringBuilder.ToString();
        }

        private void ReadWhiteSpace()
        {
            while (char.IsWhiteSpace(Current))
            {
                m_position++;
            }

            m_kind = SyntaxKind.WhitespaceToken;
        }

        private void ReadNumber()
        {
            while (char.IsDigit(Current))
            {
                m_position++;
            }

            var length = m_position - m_start;
            var text = m_text.ToString(m_start, length);

            if (!int.TryParse(text, out var value))
            {
                m_diagnostics.ReportInvalidNumber(new TextSpan(m_start, length), text, TypeSymbol.Interger);
            }

            m_value = value;
            m_kind = SyntaxKind.NumberToken;
        }

        private void ReadIdentifierOrKeyword()
        {
            while (char.IsLetter(Current))
            {
                m_position++;
            }

            var length = m_position - m_start;
            var text = m_text.ToString(m_start, length);

            m_kind = SyntaxFacts.GetKeywordKind(text);
        }
    }
}
