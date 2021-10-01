using System.Collections.Generic;

namespace Cocoa.CodeAnalysis.Syntax
{
    internal sealed class Parser
    {
        private readonly SyntaxToken[] m_tokens;

        private DiagnosticBag m_diagnostics = new DiagnosticBag();
        private int m_position;

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

        public DiagnosticBag Diagnostics => m_diagnostics;

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

            m_diagnostics.ReportUnexpcetedToken(Current.Span, Current.Kind, kind);
            return new SyntaxToken(kind, Current.Position, null, null);
        }

        public SyntaxTree Parse()
        {
            var expression = ParseExpression();
            var endOfFileToken = MatchToken(SyntaxKind.EndOfFileToken);

            return new SyntaxTree(m_diagnostics, expression, endOfFileToken);
        }

        private ExpressionSyntax ParseExpression()
        {
            return ParseAssignmentExpression();
        }

        private ExpressionSyntax ParseAssignmentExpression()
        {
            if (Peek(0).Kind == SyntaxKind.IdentifierToken &&
                Peek(1).Kind == SyntaxKind.EqualsToken)
            {
                var identifierToken = NextToken();
                var operatorToken = NextToken();
                var right = ParseAssignmentExpression();

                return new AssignmentExpressionSyntax(identifierToken, operatorToken, right);
            }

            return ParseBinaryExpression();
        }

        private ExpressionSyntax ParseBinaryExpression(int parentPrecedence = 0)
        {
            ExpressionSyntax left;
            var unaryOperatorPrecedence = Current.Kind.GetUnaryOperatorPrecedence();
            if (unaryOperatorPrecedence != 0 && unaryOperatorPrecedence >= parentPrecedence)
            {
                var operatorToken = NextToken();
                var operand = ParseBinaryExpression(unaryOperatorPrecedence);
                left = new UnaryExpressionSyntax(operatorToken, operand);
            }
            else
            {
                left = ParsePrimaryExpression();
            }

            while (true)
            {
                var precedence = Current.Kind.GetBinaryOperatorPrecedence();
                if (precedence == 0 || precedence <= parentPrecedence)
                {
                    break;
                }

                var operatorToken = NextToken();
                var right = ParseBinaryExpression(precedence);
                left = new BinaryExpressionSyntax(left, operatorToken, right);
            }

            return left;
        }

        private ExpressionSyntax ParsePrimaryExpression()
        {
            switch (Current.Kind)
            {
                case SyntaxKind.OpenParenthesisToken:
                {
                    var left = NextToken();
                    var expression = ParseExpression();
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

                case SyntaxKind.IdentifierToken:
                {
                    var identifierToken = NextToken();
                    return new NameExpressionSyntax(identifierToken);
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
