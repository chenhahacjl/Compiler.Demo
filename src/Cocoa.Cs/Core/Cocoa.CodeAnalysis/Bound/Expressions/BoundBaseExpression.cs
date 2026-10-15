using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;

namespace Cocoa.CodeAnalysis.Bound
{
    /// <summary>
    /// base 表达式：`base.Method()`（非虚调用基类成员）。
    /// </summary>
    public sealed class BoundBaseExpression : BoundExpression
    {
        public BoundBaseExpression(SyntaxNode syntax, NamedTypeSymbol type)
            : base(syntax)
        {
            Type = type;
        }

        public override BoundNodeKind Kind => BoundNodeKind.BaseExpression;
        public override TypeSymbol Type { get; }
    }
}
