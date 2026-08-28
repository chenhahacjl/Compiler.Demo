using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    public sealed class BoundConversionExpression : BoundExpression
    {
        public BoundConversionExpression(SyntaxNode syntax, TypeSymbol type, BoundExpression expression)
            : base(syntax)
        {
            Type = type;
            Expression = expression;
        }

        public override BoundNodeKind Kind => BoundNodeKind.ConversionExpression;
        public override TypeSymbol Type { get; }

        public BoundExpression Expression { get; }
    }
}
