using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 对象创建表达式：`new Foo(args)`。
    /// </summary>
    internal sealed class BoundObjectCreationExpression : BoundExpression
    {
        public BoundObjectCreationExpression(SyntaxNode syntax, NamedTypeSymbol type, ImmutableArray<BoundExpression> arguments)
            : base(syntax)
        {
            Type = type;
            Arguments = arguments;
        }

        public override BoundNodeKind Kind => BoundNodeKind.ObjectCreationExpression;
        public override TypeSymbol Type { get; }

        public ImmutableArray<BoundExpression> Arguments { get; }
    }
}
