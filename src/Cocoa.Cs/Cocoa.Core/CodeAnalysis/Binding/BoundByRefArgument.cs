using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// byref 实参（6e-M23 R3）：`out x` / `ref arr[i]` 的绑定产物——包裹可赋值 lvalue，
    /// 仅允许出现在调用实参位且对应 IsOut/IsRef 形参；Type 透传内层表达式类型。
    /// </summary>
    internal sealed class BoundByRefArgument : BoundExpression
    {
        public BoundByRefArgument(SyntaxNode syntax, BoundExpression expression, bool isRef)
            : base(syntax)
        {
            Expression = expression;
            IsRef = isRef;
        }

        public override BoundNodeKind Kind => BoundNodeKind.ByRefArgument;
        public override TypeSymbol Type => Expression.Type;

        public BoundExpression Expression { get; }
        public bool IsRef { get; }

        public bool IsOut => !IsRef;
    }
}
