using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Green;

namespace Cocoa.CodeAnalysis.Syntax.Nodes
{
    public sealed partial class FinallyClauseSyntax : SyntaxNode
    {
        internal FinallyClauseSyntax(SyntaxTree syntaxTree, SyntaxToken finallyKeyword, BlockStatementSyntax body)
            : base(syntaxTree)
        {
            FinallyKeyword = finallyKeyword;
            Body = body;
        }

        public override SyntaxKind Kind => SyntaxKind.FinallyClause;

        public SyntaxToken FinallyKeyword { get; }
        public BlockStatementSyntax Body { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return FinallyKeyword;
            yield return Body;
        }
    }
}

