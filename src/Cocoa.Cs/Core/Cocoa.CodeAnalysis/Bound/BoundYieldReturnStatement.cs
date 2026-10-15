using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;

namespace Cocoa.CodeAnalysis.Bound
{
    public sealed class BoundYieldReturnStatement : BoundStatement
    {
        public BoundYieldReturnStatement(SyntaxNode syntax, BoundExpression expression)
            : base(syntax)
        {
            Expression = expression;
        }

        public override BoundNodeKind Kind => BoundNodeKind.YieldReturnStatement;

        public BoundExpression Expression { get; }
    }
}
