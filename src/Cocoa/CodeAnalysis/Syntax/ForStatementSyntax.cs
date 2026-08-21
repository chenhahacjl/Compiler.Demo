namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// Cocoa 式 for 循环：`for [var i =] low to high`，括号与 `var i =` 均可选。
    /// </summary>
    public sealed partial class ForStatementSyntax : StatementSyntax
    {
        internal ForStatementSyntax(SyntaxTree syntaxTree, SyntaxToken keyword, SyntaxToken? openParenToken, SyntaxToken? varKeyword, SyntaxToken? identifier, SyntaxToken? equalsToken, ExpressionSyntax lowerBound, SyntaxToken toKeyword, ExpressionSyntax upperBound, SyntaxToken? stepKeyword, ExpressionSyntax? step, SyntaxToken? closeParenToken, StatementSyntax body)
            : base(syntaxTree)
        {
            Keyword = keyword;
            OpenParenToken = openParenToken;
            VarKeyword = varKeyword;
            Identifier = identifier;
            EqualsToken = equalsToken;
            LowerBound = lowerBound;
            ToKeyword = toKeyword;
            UpperBound = upperBound;
            StepKeyword = stepKeyword;
            Step = step;
            CloseParenToken = closeParenToken;
            Body = body;
        }

        public override SyntaxKind Kind => SyntaxKind.ForStatement;

        public SyntaxToken Keyword { get; }
        public SyntaxToken? OpenParenToken { get; }
        public SyntaxToken? VarKeyword { get; }
        public SyntaxToken? Identifier { get; }
        public SyntaxToken? EqualsToken { get; }
        public ExpressionSyntax LowerBound { get; }
        public SyntaxToken ToKeyword { get; }
        public ExpressionSyntax UpperBound { get; }
        public SyntaxToken? StepKeyword { get; }
        public ExpressionSyntax? Step { get; }
        public SyntaxToken? CloseParenToken { get; }
        public StatementSyntax Body { get; }
    }
}
