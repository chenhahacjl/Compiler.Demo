using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    public sealed partial class ForeachStatementSyntax : StatementSyntax
    {
        internal ForeachStatementSyntax(
            SyntaxTree syntaxTree,
            SyntaxToken foreachKeyword,
            SyntaxToken openParenthesisToken,
            SyntaxToken identifier,
            SyntaxToken inKeyword,
            ExpressionSyntax expression,
            SyntaxToken closeParenthesisToken,
            StatementSyntax body)
            : base(syntaxTree)
        {
            ForeachKeyword = foreachKeyword;
            OpenParenthesisToken = openParenthesisToken;
            Identifier = identifier;
            InKeyword = inKeyword;
            Expression = expression;
            CloseParenthesisToken = closeParenthesisToken;
            Body = body;
        }

        public override SyntaxKind Kind => SyntaxKind.ForeachStatement;
        public SyntaxToken ForeachKeyword { get; }
        public SyntaxToken OpenParenthesisToken { get; }
        public SyntaxToken Identifier { get; }
        public SyntaxToken InKeyword { get; }
        public ExpressionSyntax Expression { get; }
        public SyntaxToken CloseParenthesisToken { get; }
        public StatementSyntax Body { get; }
    }
}
