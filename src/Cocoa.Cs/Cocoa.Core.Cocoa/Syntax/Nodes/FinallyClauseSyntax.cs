using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    public sealed partial class FinallyClauseSyntax : CocoaSyntaxNode
    {
        internal FinallyClauseSyntax(SyntaxTree syntaxTree, SyntaxToken finallyKeyword, BlockStatementSyntax body)
            : base(syntaxTree)
        {
            FinallyKeyword = finallyKeyword;
            Body = body;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.FinallyClause;

        public SyntaxToken FinallyKeyword { get; }
        public BlockStatementSyntax Body { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return FinallyKeyword;
            yield return Body;
        }
    }
}

