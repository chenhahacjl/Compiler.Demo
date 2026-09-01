using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// Cocoa 寮?for 寰幆锛歚for [var i =] low to high`锛屾嫭鍙蜂笌 `var i =` 鍧囧彲閫夈€?
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

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.ForStatement;

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

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Keyword;
            if (OpenParenToken != null)
            {
                yield return OpenParenToken;
            }
            if (VarKeyword != null)
            {
                yield return VarKeyword;
            }
            if (Identifier != null)
            {
                yield return Identifier;
            }
            if (EqualsToken != null)
            {
                yield return EqualsToken;
            }
            yield return LowerBound;
            yield return ToKeyword;
            yield return UpperBound;
            if (StepKeyword != null)
            {
                yield return StepKeyword;
            }
            if (Step != null)
            {
                yield return Step;
            }
            if (CloseParenToken != null)
            {
                yield return CloseParenToken;
            }
            yield return Body;
        }
    }
}

