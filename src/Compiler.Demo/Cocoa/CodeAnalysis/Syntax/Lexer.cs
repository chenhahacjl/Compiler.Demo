using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Syntax
{
    internal sealed class Lexer
    {
        private readonly string m_text;
        private int m_position;
        private DiagnosticBag m_diagnostics = new DiagnosticBag();

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

        private void Next() => m_position++;

        public SyntaxToken Lex()
        {
            if (m_position >= m_text.Length)
            {
                return new SyntaxToken(SyntaxKind.EndOfFileToken, m_position, "\0", null);
            }

            var start = m_position;

            if (char.IsDigit(Current))
            {
                while (char.IsDigit(Current))
                {
                    Next();
                }

                var length = m_position - start;
                var text = m_text.Substring(start, length);

                if (!int.TryParse(text, out var value))
                {
                    m_diagnostics.ReportInvalidNumber(new TextSpan(start, length), m_text, typeof(int));
                }

                return new SyntaxToken(SyntaxKind.NumberToken, start, text, value);
            }

            if (char.IsWhiteSpace(Current))
            {
                while (char.IsWhiteSpace(Current))
                {
                    Next();
                }

                var length = m_position - start;
                var text = m_text.Substring(start, length);

                int.TryParse(text, out var value);

                return new SyntaxToken(SyntaxKind.WhitespaceToken, start, text, value);
            }

            if (char.IsLetter(Current))
            {
                while (char.IsLetter(Current))
                {
                    Next();
                }

                var length = m_position - start;
                var text = m_text.Substring(start, length);
                var kind = SyntaxFacts.GetKeywordKind(text);

                return new SyntaxToken(kind, start, text, null);
            }

            switch (Current)
            {
                case '+': return new SyntaxToken(SyntaxKind.PlusToken, m_position++, "+", null);
                case '-': return new SyntaxToken(SyntaxKind.MinusToken, m_position++, "-", null);
                case '*': return new SyntaxToken(SyntaxKind.StarToken, m_position++, "*", null);
                case '/': return new SyntaxToken(SyntaxKind.SlashToken, m_position++, "/", null);
                case '(': return new SyntaxToken(SyntaxKind.OpenParenthesisToken, m_position++, "(", null);
                case ')': return new SyntaxToken(SyntaxKind.CloseParenthesisToken, m_position++, ")", null);
                case '&':
                {
                    if (Lookahead == '&')
                    {
                        m_position += 2;
                        return new SyntaxToken(SyntaxKind.AmpersandAmpersandToken, start, "&&", null);
                    }
                    break;
                }
                case '|':
                {
                    if (Lookahead == '|')
                    {
                        m_position += 2;
                        return new SyntaxToken(SyntaxKind.PipePipeToken, start, "||", null);
                    }
                    break;
                }
                case '=':
                {
                    if (Lookahead == '=')
                    {
                        m_position += 2;
                        return new SyntaxToken(SyntaxKind.EqualsEqualsToken, start, "==", null);
                    }
                    else
                    {
                        return new SyntaxToken(SyntaxKind.EqualsToken, m_position++, "=", null);
                    }
                }
                case '!':
                {
                    if (Lookahead == '=')
                    {
                        m_position += 2;
                        return new SyntaxToken(SyntaxKind.BangEqualsToken, start, "!=", null);
                    }
                    else
                    {
                        return new SyntaxToken(SyntaxKind.BangToken, m_position++, "!", null);
                    }
                }
            }

            m_diagnostics.ReportBadCharacter(m_position, Current);
            return new SyntaxToken(SyntaxKind.BadToken, m_position++, m_text.Substring(m_position - 1, 1), null);
        }
    }
}
