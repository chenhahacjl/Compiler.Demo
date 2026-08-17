using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundMemberCallExpression : BoundExpression
    {
        public BoundMemberCallExpression(SyntaxNode syntax, BoundExpression expression, string identifier, ImmutableArray<BoundExpression> arguments, TypeSymbol type)
            : base(syntax)
        {
            Expression = expression;
            Identifier = identifier;
            Arguments = arguments;
            Type = type;
        }

        public override BoundNodeKind Kind => BoundNodeKind.MemberCallExpression;
        public override TypeSymbol Type { get; }

        public BoundExpression Expression { get; }
        public string Identifier { get; }
        public ImmutableArray<BoundExpression> Arguments { get; }
    }
}