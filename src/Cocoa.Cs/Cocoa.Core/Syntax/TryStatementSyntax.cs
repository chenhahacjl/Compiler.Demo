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
    }
}
