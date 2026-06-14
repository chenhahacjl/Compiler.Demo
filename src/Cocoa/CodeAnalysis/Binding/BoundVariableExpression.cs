using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    internal sealed class BoundVariableExpression : BoundExpression
    {
        public BoundVariableExpression(SyntaxNode syntax, VariableSymbol variable)
            : base(syntax)
        {
            Variable = variable;
        }

        public override BoundNodeKind Kind => BoundNodeKind.VariableExpression;
        public override TypeSymbol Type => Variable.Type;

        public VariableSymbol Variable { get; }

        public override BoundConstant? ConstantValue => Variable.Constant;
    }
}
