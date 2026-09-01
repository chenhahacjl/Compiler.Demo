namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>case 瀛愬彞锛歚case 甯搁噺 [when 鏉′欢] : 浣揱锛堟敮鎸?`case 1: case 2:` 鍙犳爣锛夈€?/summary>
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

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return CaseKeyword;
            foreach (var child in Values.GetWithSeparators())
            {
                yield return child;
            }
            if (WhenKeyword != null)
            {
                yield return WhenKeyword;
            }
            if (WhenCondition != null)
            {
                yield return WhenCondition;
            }
            yield return ColonToken;
            yield return Body;
        }
    }
}
