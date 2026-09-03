using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 函数值间接调用（6e-M22 C4）：`f(x)` / `obj.handler(x)` —— 被调者为函数类型表达式。
    /// 语义 = 单方法接口 Invoke：求值被调者 → 以其接收者为 this 调用目标方法。
    /// </summary>
    public sealed class BoundInvocationExpression : BoundExpression
    {
        public BoundInvocationExpression(SyntaxNode syntax, BoundExpression callee, ImmutableArray<BoundExpression> arguments, TypeSymbol type)
            : base(syntax)
        {
            Callee = callee;
            Arguments = arguments;
            Type = type;
        }

        public override BoundNodeKind Kind => BoundNodeKind.InvocationExpression;

        public override TypeSymbol Type { get; }

        public BoundExpression Callee { get; }

        public ImmutableArray<BoundExpression> Arguments { get; }
    }
}
