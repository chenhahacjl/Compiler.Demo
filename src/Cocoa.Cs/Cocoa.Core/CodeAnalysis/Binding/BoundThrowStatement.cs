using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundThrowStatement : BoundStatement
    {
        public BoundThrowStatement(SyntaxNode syntax, BoundExpression expression)
            : base(syntax)
        {
            Expression = expression;
        }

        public override BoundNodeKind Kind => BoundNodeKind.ThrowStatement;

        public BoundExpression Expression { get; }
    }
}
