using Cocoa.CodeAnalysis.Symbols;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定表达式
    /// </summary>
    internal abstract class BoundExpression : BoundNode
    {
        public abstract TypeSymbol Type { get; }
        public virtual BoundConstant? ConstantValue => null;
    }
}
