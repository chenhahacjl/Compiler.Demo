using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 数组索引表达式（读）：a[i]
    /// </summary>
    public sealed class BoundElementAccessExpression : BoundExpression
    {
        public BoundElementAccessExpression(SyntaxNode syntax, TypeSymbol type, BoundExpression target, BoundExpression index)
            : base(syntax)
        {
            Type = type;
            Target = target;
            Index = index;
        }

        public override BoundNodeKind Kind => BoundNodeKind.ElementAccessExpression;
        public override TypeSymbol Type { get; }

        public BoundExpression Target { get; }
        public BoundExpression Index { get; }
    }
}
