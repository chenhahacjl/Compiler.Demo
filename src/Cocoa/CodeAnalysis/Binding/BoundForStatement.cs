using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundForStatement : BoundLoopStatement
    {
        public BoundForStatement(SyntaxNode syntax, VariableSymbol variable, BoundExpression lowerBound, BoundExpression upperBound, BoundExpression? step, BoundStatement body, BoundLabel breakLabel, BoundLabel continueLabel)
            : base(syntax, breakLabel, continueLabel)
        {
            Variable = variable;
            LowerBound = lowerBound;
            UpperBound = upperBound;
            Step = step;
            Body = body;
        }

        public override BoundNodeKind Kind => BoundNodeKind.ForStatement;

        public VariableSymbol Variable { get; }
        public BoundExpression LowerBound { get; }
        public BoundExpression UpperBound { get; }
        public BoundExpression? Step { get; }
        public BoundStatement Body { get; }
    }
}