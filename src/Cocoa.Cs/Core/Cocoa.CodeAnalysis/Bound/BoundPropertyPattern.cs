using System.Collections.Immutable;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Bound
{
    /// <summary>
    /// 属性模式绑定：expr is { Length: > 0 }
    /// 检查对象属性是否匹配指定的子模式
    /// </summary>
    public sealed class BoundPropertyPattern : BoundExpression
    {
        public BoundPropertyPattern(SyntaxNode syntax, BoundExpression expression, ImmutableArray<BoundPropertySubpattern> subpatterns)
            : base(syntax)
        {
            Expression = expression;
            Subpatterns = subpatterns;
        }

        public override BoundNodeKind Kind => BoundNodeKind.PropertyPattern;
        public override TypeSymbol Type => TypeSymbol.Boolean;

        public BoundExpression Expression { get; }
        public ImmutableArray<BoundPropertySubpattern> Subpatterns { get; }
    }
}