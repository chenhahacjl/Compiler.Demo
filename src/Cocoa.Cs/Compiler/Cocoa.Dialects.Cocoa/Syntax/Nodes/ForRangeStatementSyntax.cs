using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// Cocoa 次数循环（内部名 forrange）：`for [var i =] low to high [step k]`，
    /// 括号与 `var i =` 均可省略；省略变量时为纯次数循环 `for 1 to 10`（隐藏计数器，即 `for _ = 1 to 10` 的简写）。
    /// 源语法仍为 `for N to M [step k]`，与 C 风格 `for (init; cond; update)` 靠头内分号 / to 区分。
    /// </summary>
    public sealed partial class ForRangeStatementSyntax : StatementSyntax
    {
        internal ForRangeStatementSyntax(SyntaxTree syntaxTree, SyntaxToken keyword, SyntaxToken? openParenToken, SyntaxToken? varKeyword, SyntaxToken? identifier, SyntaxToken? equalsToken, ExpressionSyntax lowerBound, SyntaxToken toKeyword, ExpressionSyntax upperBound, SyntaxToken? stepKeyword, ExpressionSyntax? step, SyntaxToken? closeParenToken, StatementSyntax body)
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

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.ForRangeStatement;

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
