using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    public sealed partial class ContinueStatementSyntax : StatementSyntax
    {
        internal ContinueStatementSyntax(SyntaxTree syntaxTree, SyntaxToken keyword)
            : base(syntaxTree)
        {
            Keyword = keyword;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.ContinueStatement;

        public SyntaxToken Keyword { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Keyword;
        }
    }
}

