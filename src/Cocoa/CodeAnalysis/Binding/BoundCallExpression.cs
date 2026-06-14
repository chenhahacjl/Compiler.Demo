using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundCallExpression : BoundExpression
    {
        public BoundCallExpression(SyntaxNode syntax, FunctionSymbol function, ImmutableArray<BoundExpression> arguments)
            : base(syntax)
        {
            Function = function;
            Arguments = arguments;
        }

        public override BoundNodeKind Kind => BoundNodeKind.CallExpression;
        public override TypeSymbol Type => Function.ReturnType;

        public FunctionSymbol Function { get; }
        public ImmutableArray<BoundExpression> Arguments { get; }
    }
}
