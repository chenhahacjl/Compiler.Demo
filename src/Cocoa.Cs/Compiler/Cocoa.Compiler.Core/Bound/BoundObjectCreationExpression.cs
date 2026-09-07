using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 对象创建表达式：`new Foo(args)`。
    /// </summary>
    public sealed class BoundObjectCreationExpression : BoundExpression
    {
        public BoundObjectCreationExpression(SyntaxNode syntax, NamedTypeSymbol type, ImmutableArray<BoundExpression> arguments, FunctionSymbol? constructor = null)
            : base(syntax)
        {
            Type = type;
            Arguments = arguments;
            Constructor = constructor;
        }

        public override BoundNodeKind Kind => BoundNodeKind.ObjectCreationExpression;
        public override TypeSymbol Type { get; }

        public ImmutableArray<BoundExpression> Arguments { get; }

        /// <summary>绑定选中的构造器（重载解析结果；序列化/重写路径可为 null，发射端按参数回退匹配）。</summary>
        public FunctionSymbol? Constructor { get; }
    }
}
