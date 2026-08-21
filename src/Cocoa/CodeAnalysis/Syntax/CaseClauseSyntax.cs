namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>case 子句：`case 常量 [when 条件] : 体`（支持 `case 1: case 2:` 叠标）。</summary>
    public sealed partial class CaseClauseSyntax : SwitchSectionSyntax
    {
        internal CaseClauseSyntax(SyntaxTree syntaxTree, SyntaxToken caseKeyword, SeparatedSyntaxList<ExpressionSyntax> values, SyntaxToken? whenKeyword, ExpressionSyntax? whenCondition, SyntaxToken colonToken, StatementSyntax body)
            : base(syntaxTree)
        {
            CaseKeyword = caseKeyword;
            Values = values;
            WhenKeyword = whenKeyword;
            WhenCondition = whenCondition;
            ColonToken = colonToken;
            Body = body;
        }

        public override SyntaxKind Kind => SyntaxKind.CaseClause;

        public SyntaxToken CaseKeyword { get; }
        public SeparatedSyntaxList<ExpressionSyntax> Values { get; }
        public SyntaxToken? WhenKeyword { get; }
        public ExpressionSyntax? WhenCondition { get; }
        public SyntaxToken ColonToken { get; }
        public StatementSyntax Body { get; }
    }
}
