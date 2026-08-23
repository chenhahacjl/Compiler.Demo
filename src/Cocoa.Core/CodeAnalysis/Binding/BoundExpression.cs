using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 绑定表达式
    /// </summary>
    internal abstract class BoundExpression : BoundNode
    {
        protected BoundExpression(SyntaxNode syntax)
            : base(syntax)
        {
        }

        public abstract TypeSymbol Type { get; }
        public virtual BoundConstant? ConstantValue => null;
    }
}
