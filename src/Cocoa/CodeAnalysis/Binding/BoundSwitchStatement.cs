using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundSwitchCase
    {
        public BoundSwitchCase(SyntaxNode syntax, BoundExpression? value, BoundStatement body)
        {
            Syntax = syntax;
            Value = value;
            Body = body;
        }

        public SyntaxNode Syntax { get; }
        public BoundExpression? Value { get; }
        public BoundStatement Body { get; }
    }

    internal sealed class BoundSwitchStatement : BoundStatement
    {
        public BoundSwitchStatement(SyntaxNode syntax, BoundExpression expression, ImmutableArray<BoundSwitchCase> cases, BoundLabel breakLabel)
            : base(syntax)
        {
            Expression = expression;
            Cases = cases;
            BreakLabel = breakLabel;
        }

        public override BoundNodeKind Kind => BoundNodeKind.SwitchStatement;
        public BoundExpression Expression { get; }
        public ImmutableArray<BoundSwitchCase> Cases { get; }
        public BoundLabel BreakLabel { get; }
    }
}
