using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Nodes;
using Cocoa.CodeAnalysis.Syntax.Green;

namespace Cocoa.CodeAnalysis.Bound
{
    /// <summary>
    /// 数组元素赋值：a[i] = v
    /// </summary>
    public sealed class BoundElementAssignmentExpression : BoundExpression
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
