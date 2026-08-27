using System.Collections.Immutable;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundTryStatement : BoundStatement
    {
        public BoundTryStatement(SyntaxNode syntax, BoundStatement tryBlock, ImmutableArray<BoundCatchClause> catches, BoundStatement? finallyBlock)
            : base(syntax)
        {
            TryBlock = tryBlock;
            Catches = catches;
            FinallyBlock = finallyBlock;
        }

        public override BoundNodeKind Kind => BoundNodeKind.TryStatement;

        public BoundStatement TryBlock { get; }
        public ImmutableArray<BoundCatchClause> Catches { get; }
        public BoundStatement? FinallyBlock { get; }
    }
}
