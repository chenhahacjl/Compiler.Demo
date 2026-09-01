using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    public sealed partial class WhileStatementSyntax : StatementSyntax
    {
        internal WhileStatementSyntax(SyntaxTree syntaxTree, SyntaxToken keyword, ExpressionSyntax condition, StatementSyntax body)
            : base(syntaxTree)
        {
            Keyword = keyword;
            Condition = condition;
            Body = body;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.WhileStatement;

        public SyntaxToken Keyword { get; }
        public ExpressionSyntax Condition { get; }
        public StatementSyntax Body { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Keyword;
            yield return Condition;
            yield return Body;
        }
    }
}

