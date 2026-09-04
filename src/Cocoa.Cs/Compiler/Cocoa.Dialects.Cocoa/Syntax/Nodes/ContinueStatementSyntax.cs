using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    public sealed partial class ContinueStatementSyntax : StatementSyntax
    {
        internal ContinueStatementSyntax(SyntaxTree syntaxTree, SyntaxToken keyword)
            : base(syntaxTree)
        {
            Keyword = keyword;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.ContinueStatement;

        public SyntaxToken Keyword { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Keyword;
        }
    }
}

