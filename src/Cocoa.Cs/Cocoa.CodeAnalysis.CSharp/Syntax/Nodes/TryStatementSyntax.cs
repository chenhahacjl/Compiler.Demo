using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
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

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.TryStatement;

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

