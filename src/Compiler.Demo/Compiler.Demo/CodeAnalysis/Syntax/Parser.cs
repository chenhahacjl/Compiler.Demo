using System.Collections.Generic;

namespace Compiler.Demo.CodeAnalysis.Syntax
{
    internal sealed class Parser
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
                token = lexer.Lex();

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

        private SyntaxToken MatchToken(SyntaxKind kind)
        {
            if (Current.Kind == kind)
            {
                return NextToken();
            }

            m_diagnostics.Add($"ERROR: Unexpected token <{Current.Kind}>, expected <{kind}>");
            return new SyntaxToken(kind, Current.Position, null, null);
        }

        public SyntaxTree Parse()
        {
            var expression = ParserExpression();
            var endOfFileToken = MatchToken(SyntaxKind.EndOfFileToken);

            return new SyntaxTree(m_diagnostics, expression, endOfFileToken);
        }

        private ExpressionSyntax ParserExpression(int parentPrecedence = 0)
        {
            ExpressionSyntax left;
            var unaryOperatorPrecedence = Current.Kind.GetUnaryOperatorPrecedence();
            if (unaryOperatorPrecedence != 0 && unaryOperatorPrecedence >= parentPrecedence)
            {
                var operatorToken = NextToken();
                var operand = ParserExpression(unaryOperatorPrecedence);
                left = new UnaryExpressionSyntax(operatorToken, operand);
            }
            else
            {
                left = ParserPrimaryExpression();
            }

            while (true)
            {
                var precedence = Current.Kind.GetBinaryOperatorPrecedence();
                if (precedence == 0 || precedence <= parentPrecedence)
                {
                    break;
                }

                var operatorToken = NextToken();
                var right = ParserExpression(precedence);
                left = new BinaryExpressionSyntax(left, operatorToken, right);
            }

            return left;
        }

        private ExpressionSyntax ParserPrimaryExpression()
        {
            switch (Current.Kind)
            {
                case SyntaxKind.OpenParenthesisToken:
                {
                    var left = NextToken();
                    var expression = ParserExpression();
                    var right = MatchToken(SyntaxKind.CloseParenthesisToken);

                    return new ParenthesizedExpressionSyntax(left, expression, right);
                }

                case SyntaxKind.TrueKeyword:
                case SyntaxKind.FalseKeyword:
                {
                    var keywordToken = NextToken();
                    var value = keywordToken.Kind == SyntaxKind.TrueKeyword;

                    return new LiteralExpressionSyntax(keywordToken, value);
                }

                default:
                {
                    var numberToken = MatchToken(SyntaxKind.NumberToken);

                    return new LiteralExpressionSyntax(numberToken);
                }
            }
        }
    }
}
