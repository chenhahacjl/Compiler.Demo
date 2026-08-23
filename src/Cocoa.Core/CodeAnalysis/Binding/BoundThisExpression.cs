using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// this 表达式：类方法/构造内对当前实例的引用。
    /// </summary>
    internal sealed class BoundThisExpression : BoundExpression
    {
        public BoundThisExpression(SyntaxNode syntax, ClassTypeSymbol type)
            : base(syntax)
        {
            Type = type;
        }

        public override BoundNodeKind Kind => BoundNodeKind.ThisExpression;
        public override TypeSymbol Type { get; }
    }
}
