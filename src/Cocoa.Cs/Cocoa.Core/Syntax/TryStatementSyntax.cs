using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    public sealed partial class TryStatementSyntax : StatementSyntax
    {
        internal TryStatementSyntax(SyntaxTree syntaxTree, SyntaxToken keyword, BlockStatementSyntax tryBlock,
                                    ImmutableArray<CatchClauseSyntax> catches, FinallyClauseSyntax? finallyClause)
            : base(syntaxTree)
        {
            Keyword = keyword;
            TryBlock = tryBlock;
            Catches = catches;
            Finally = finallyClause;
        }

        public override SyntaxKind Kind => SyntaxKind.TryStatement;

        public SyntaxToken Keyword { get; }
        public BlockStatementSyntax TryBlock { get; }
        public ImmutableArray<CatchClauseSyntax> Catches { get; }
        public FinallyClauseSyntax? Finally { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Keyword;
            yield return TryBlock;
            foreach (var child in Catches)
            {
                yield return child;
            }
            if (Finally != null)
            {
                yield return Finally;
            }
        }
    }
}
