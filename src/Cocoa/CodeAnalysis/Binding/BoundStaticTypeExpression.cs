using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 静态类型表达式：`MathHelpers.Square(2)` 中的 `MathHelpers`（类型名，无实例值）。
    /// </summary>
    internal sealed class BoundStaticTypeExpression : BoundExpression
    {
        public BoundStaticTypeExpression(SyntaxNode syntax, ClassTypeSymbol type)
            : base(syntax)
        {
            Type = type;
        }

        public override BoundNodeKind Kind => BoundNodeKind.StaticTypeExpression;
        public override TypeSymbol Type { get; }
    }
}
