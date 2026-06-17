using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    public sealed partial class SwitchStatementSyntax : StatementSyntax
    {
        internal SwitchStatementSyntax(
            SyntaxTree syntaxTree,
            SyntaxToken switchKeyword,
            SyntaxToken openParenthesisToken,
            ExpressionSyntax expression,
            SyntaxToken closeParenthesisToken,
            SyntaxToken openBraceToken,
            ImmutableArray<CaseClauseSyntax> clauses,
            SyntaxToken closeBraceToken)
            : base(syntaxTree)
        {
            SwitchKeyword = switchKeyword;
            OpenParenthesisToken = openParenthesisToken;
            Expression = expression;
            CloseParenthesisToken = closeParenthesisToken;
            OpenBraceToken = openBraceToken;
            Clauses = clauses;
            CloseBraceToken = closeBraceToken;
        }

        public override SyntaxKind Kind => SyntaxKind.SwitchStatement;
        public SyntaxToken SwitchKeyword { get; }
        public SyntaxToken OpenParenthesisToken { get; }
        public ExpressionSyntax Expression { get; }
        public SyntaxToken CloseParenthesisToken { get; }
        public SyntaxToken OpenBraceToken { get; }
        public ImmutableArray<CaseClauseSyntax> Clauses { get; }
        public SyntaxToken CloseBraceToken { get; }
    }
}
