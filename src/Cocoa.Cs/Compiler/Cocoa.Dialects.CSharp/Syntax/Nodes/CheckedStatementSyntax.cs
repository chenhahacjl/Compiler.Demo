using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    public sealed partial class CheckedStatementSyntax : StatementSyntax
    {
        internal CheckedStatementSyntax(SyntaxTree syntaxTree, SyntaxToken keyword, BlockStatementSyntax body)
            : base(syntaxTree)
        {
            Keyword = keyword;
            Body = body;
        }

        public override CSharpSyntaxKind Kind => Keyword.Kind == (SyntaxKind)CSharpSyntaxKind.CheckedKeyword
            ? CSharpSyntaxKind.CheckedStatement
            : CSharpSyntaxKind.UncheckedStatement;

        public SyntaxToken Keyword { get; }
        public BlockStatementSyntax Body { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Keyword;
            yield return Body;
        }
    }
}
