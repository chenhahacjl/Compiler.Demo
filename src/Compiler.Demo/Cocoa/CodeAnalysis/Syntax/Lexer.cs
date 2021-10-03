﻿using Cocoa.CodeAnalysis.Text;
using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Syntax
{
    internal sealed class Lexer
    {
        private readonly DiagnosticBag m_diagnostics = new DiagnosticBag();
        private readonly string m_text;

        private int m_position;

        private int m_start;
        private SyntaxKind m_kind;
        private object m_value;

        public Lexer(string text)
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
                case '&':
                {
                    if (Lookahead == '&')
                    {
                        m_kind = SyntaxKind.AmpersandAmpersandToken;
                        m_position += 2;
                        break;
                    }
                    break;
                }
                case '|':
                {
                    if (Lookahead == '|')
                    {
                        m_kind = SyntaxKind.PipePipeToken;
                        m_position += 2;
                        break;
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
                        m_kind = SyntaxKind.EqualsEqualsToken;
                        m_position++;
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
                    ReadNumberToken();
                    break;
                }
                case ' ':
                case '\t':
                case '\n':
                case '\r':
                {
                    ReadWhitespace();
                    break;
                }
                default:
                {
                    if (char.IsLetter(Current))
                    {
                        ReadIdentifierKeyword();
                    }
                    else if (char.IsWhiteSpace(Current))
                    {
                        ReadWhitespace();
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
                text = m_text.Substring(m_start, length);
            }

            return new SyntaxToken(m_kind, m_start, text, m_value);
        }
        private void ReadWhitespace()
        {
            while (char.IsWhiteSpace(Current))
            {
                m_position++;
            }

            m_kind = SyntaxKind.WhitespaceToken;
        }

        private void ReadNumberToken()
        {
            while (char.IsDigit(Current))
            {
                m_position++;
            }

            var length = m_position - m_start;
            var text = m_text.Substring(m_start, length);

            if (!int.TryParse(text, out var value))
            {
                m_diagnostics.ReportInvalidNumber(new TextSpan(m_start, length), m_text, typeof(int));
            }

            m_value = value;
            m_kind = SyntaxKind.NumberToken;
        }

        private void ReadIdentifierKeyword()
        {
            while (char.IsLetter(Current))
            {
                m_position++;
            }

            var length = m_position - m_start;
            var text = m_text.Substring(m_start, length);

            m_kind = SyntaxFacts.GetKeywordKind(text);
        }
    }
}
