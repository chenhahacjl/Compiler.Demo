using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
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
