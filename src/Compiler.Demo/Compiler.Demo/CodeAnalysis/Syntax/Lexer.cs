using System.Collections.Generic;

namespace Compiler.Demo.CodeAnalysis.Syntax
{
    internal sealed class Lexer
    {
        private readonly string m_text;
        private int m_position;
        private List<string> m_diagnostics = new List<string>();

        public Lexer(string text)
        {
            m_text = text;
        }

        public IEnumerable<string> Diagnostics => m_diagnostics;

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

            if (char.IsDigit(Current))
            {
                var start = m_position;

                while (char.IsDigit(Current))
                {
                    Next();
                }

                var length = m_position - start;
                var text = m_text.Substring(start, length);

                if (!int.TryParse(text, out var value))
                {
                    m_diagnostics.Add($"The number {m_text} isn't valid Int32");
                }

                return new SyntaxToken(SyntaxKind.NumberToken, start, text, value);
            }

            if (char.IsWhiteSpace(Current))
            {
                var start = m_position;

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
                var start = m_position;

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
                    if (Lookahead == '&') return new SyntaxToken(SyntaxKind.AmpersandAmpersandToken, m_position += 2, "&&", null);
                    break;
                }
                case '|':
                {
                    if (Lookahead == '|') return new SyntaxToken(SyntaxKind.PipePipeToken, m_position += 2, "||", null);
                    break;
                }
                case '=':
                {
                    if (Lookahead == '=') return new SyntaxToken(SyntaxKind.EqualsEqualsToken, m_position += 2, "==", null);
                    break;
                }
                case '!':
                {
                    if (Lookahead == '=') return new SyntaxToken(SyntaxKind.BangEqualsToken, m_position += 2, "!=", null);
                    else return new SyntaxToken(SyntaxKind.BangToken, m_position++, "!", null);
                }
            }

            m_diagnostics.Add($"ERROR: Bad character input: '{Current}'");
            return new SyntaxToken(SyntaxKind.BadToken, m_position++, m_text.Substring(m_position - 1, 1), null);
        }
    }
}
