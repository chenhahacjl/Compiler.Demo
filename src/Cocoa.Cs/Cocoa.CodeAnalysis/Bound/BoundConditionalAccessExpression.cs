using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 空条件成员访问：expr?.Member / expr?.Method(args)
    /// </summary>
    public sealed class BoundConditionalAccessExpression : BoundExpression
    {
        public BoundConditionalAccessExpression(SyntaxNode syntax, BoundExpression expression, BoundExpression whenNotNull)
            : base(syntax)
        {
            Expression = expression;
            WhenNotNull = whenNotNull;
        }

        public override BoundNodeKind Kind => BoundNodeKind.ConditionalAccessExpression;
        public override TypeSymbol Type => WhenNotNull.Type;

        public BoundExpression Expression { get; }
        public BoundExpression WhenNotNull { get; }
    }
}
