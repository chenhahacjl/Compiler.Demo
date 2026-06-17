using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundForeachStatement : BoundStatement
    {
        public BoundForeachStatement(SyntaxNode syntax, VariableSymbol variable, BoundExpression expression, BoundStatement body, BoundLabel breakLabel, BoundLabel continueLabel)
            : base(syntax)
        {
            Variable = variable;
            Expression = expression;
            Body = body;
            BreakLabel = breakLabel;
            ContinueLabel = continueLabel;
        }

        public override BoundNodeKind Kind => BoundNodeKind.ForeachStatement;
        public VariableSymbol Variable { get; }
        public BoundExpression Expression { get; }
        public BoundStatement Body { get; }
        public BoundLabel BreakLabel { get; }
        public BoundLabel ContinueLabel { get; }
    }
}
