using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    public sealed partial class FinallyClauseSyntax : CSharpSyntaxNode
    {
        internal FinallyClauseSyntax(SyntaxTree syntaxTree, SyntaxToken finallyKeyword, BlockStatementSyntax body)
            : base(syntaxTree)
        {
            FinallyKeyword = finallyKeyword;
            Body = body;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.FinallyClause;

        public SyntaxToken FinallyKeyword { get; }
        public BlockStatementSyntax Body { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return FinallyKeyword;
            yield return Body;
        }
    }
}

