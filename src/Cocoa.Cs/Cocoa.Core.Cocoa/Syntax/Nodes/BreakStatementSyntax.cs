using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    public sealed partial class BreakStatementSyntax : StatementSyntax
    {
        internal BreakStatementSyntax(SyntaxTree syntaxTree, SyntaxToken keyword)
            : base(syntaxTree)
        {
            Keyword = keyword;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.BreakStatement;

        public SyntaxToken Keyword { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Keyword;
        }
    }
}

