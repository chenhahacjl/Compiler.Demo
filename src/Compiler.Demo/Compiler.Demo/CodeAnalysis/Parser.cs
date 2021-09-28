using System.Collections.Generic;

namespace Compiler.Demo.CodeAnalysis
{
    public class Parser
    {
        private readonly SyntaxToken[] m_tokens;
        private int m_position;
        private List<string> m_diagnostics = new List<string>();

        public Parser(string text)
        {
            var tokens = new List<SyntaxToken>();

            var lexer = new Lexer(text);
            SyntaxToken token;

            do
            {
                token = lexer.NextToken();

                if (token.Kind != SyntaxKind.WhitespaceToken &&
                    token.Kind != SyntaxKind.BadToken)
                {
                    tokens.Add(token);
                }

            } while (token.Kind != SyntaxKind.EndOfFileToken);

            m_tokens = tokens.ToArray();
            m_diagnostics.AddRange(lexer.Diagnostics);
        }

        public IEnumerable<string> Diagnostics => m_diagnostics;

        private SyntaxToken Peek(int offset)
        {
            var index = m_position + offset;
            if (index >= m_tokens.Length)
            {
                return m_tokens[m_tokens.Length - 1];
            }

            return m_tokens[index];
        }

        private SyntaxToken Current => Peek(0);

        private SyntaxToken NextToken()
        {
            var currnet = Current;
            m_position++;

            return currnet;
        }

        private SyntaxToken Match(SyntaxKind kind)
        {
            if (Current.Kind == kind)
            {
                return NextToken();
            }

            m_diagnostics.Add($"ERROR: Unexpected token <{Current.Kind}>, expected <{kind}>");
            return new SyntaxToken(kind, Current.Position, null, null);
        }

        private ExpressionSyntax ParseExpression()
        {
            return ParserTerm();
        }

        public SyntaxTree Parse()
        {
            var expression = ParserTerm();
            var endOfFileToken = Match(SyntaxKind.EndOfFileToken);

            return new SyntaxTree(m_diagnostics, expression, endOfFileToken);
        }

        private ExpressionSyntax ParserTerm()
        {
            var left = ParserFactor();

            while (Current.Kind == SyntaxKind.PlusToken ||
                    Current.Kind == SyntaxKind.MinusToken)
            {
                var operatorToken = NextToken();
                var right = ParserFactor();
                left = new BinaryExpressionSyntax(left, operatorToken, right);
            }

            return left;
        }

        private ExpressionSyntax ParserFactor()
        {
            var left = ParserPrimaryExpression();

            while (Current.Kind == SyntaxKind.StarToken ||
                    Current.Kind == SyntaxKind.SlashToken)
            {
                var operatorToken = NextToken();
                var right = ParserPrimaryExpression();
                left = new BinaryExpressionSyntax(left, operatorToken, right);
            }

            return left;
        }

        private ExpressionSyntax ParserPrimaryExpression()
        {
            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
            {
                var left = NextToken();
                var expression = ParseExpression();
                var right = Match(SyntaxKind.CloseParenthesisToken);

                return new ParenthesizedExpressionSyntax(left, expression, right);
            }

            var numberToken = Match(SyntaxKind.NumberToken);
            return new NumberExpressionSyntax(numberToken);
        }
    }
}
