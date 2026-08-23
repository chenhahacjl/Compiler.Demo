using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 字段赋值表达式：`point._x = 5`。
    /// </summary>
    internal sealed class BoundMemberAssignmentExpression : BoundExpression
    {
        public BoundMemberAssignmentExpression(SyntaxNode syntax, BoundExpression target, FieldSymbol field, BoundExpression expression)
            : base(syntax)
        {
            Target = target;
            Field = field;
            Expression = expression;
        }

        public override BoundNodeKind Kind => BoundNodeKind.MemberAssignmentExpression;
        public override TypeSymbol Type => Expression.Type;

        public BoundExpression Target { get; }
        public FieldSymbol Field { get; }
        public BoundExpression Expression { get; }
    }
}
