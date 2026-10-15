using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;

namespace Cocoa.CodeAnalysis.Bound
{
    public sealed class BoundDoWhileStatement : BoundLoopStatement
    {
        public BoundDoWhileStatement(SyntaxNode syntax, BoundStatement body, BoundExpression condition, BoundLabel breakLabel, BoundLabel continueLabel)
            : base(syntax, breakLabel, continueLabel)
        {
            Body = body;
            Condition = condition;
        }

        public override BoundNodeKind Kind => BoundNodeKind.DoWhileStatement;

        public BoundStatement Body { get; }
        public BoundExpression Condition { get; }
    }
}