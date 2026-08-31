using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    public sealed class BoundReturnStatement : BoundStatement
    {
        public BoundReturnStatement(SyntaxNode syntax, BoundExpression? expression)
            : base(syntax)
        {
            Expression = expression;
        }

        public override BoundNodeKind Kind => BoundNodeKind.ReturnStatement;

        public BoundExpression? Expression { get; }
    }
}
