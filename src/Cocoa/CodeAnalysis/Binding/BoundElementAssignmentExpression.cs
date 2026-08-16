using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 数组元素赋值：a[i] = v
    /// </summary>
    internal sealed class BoundElementAssignmentExpression : BoundExpression
    {
        public BoundElementAssignmentExpression(SyntaxNode syntax, TypeSymbol type, BoundElementAccessExpression target, BoundExpression expression)
            : base(syntax)
        {
            Type = type;
            Target = target;
            Expression = expression;
        }

        public override BoundNodeKind Kind => BoundNodeKind.ElementAssignmentExpression;
        public override TypeSymbol Type { get; }

        public BoundElementAccessExpression Target { get; }
        public BoundExpression Expression { get; }
    }
}