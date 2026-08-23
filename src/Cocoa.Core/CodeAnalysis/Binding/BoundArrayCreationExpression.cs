using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;
using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 数组创建表达式：new int[3] / new int[] {1, 2, 3}
    /// </summary>
    internal sealed class BoundArrayCreationExpression : BoundExpression
    {
        public BoundArrayCreationExpression(SyntaxNode syntax, TypeSymbol type, BoundExpression length, ImmutableArray<BoundExpression> initializers)
            : base(syntax)
        {
            Type = type;
            Length = length;
            Initializers = initializers;
        }

        public override BoundNodeKind Kind => BoundNodeKind.ArrayCreationExpression;
        public override TypeSymbol Type { get; }

        public BoundExpression Length { get; }
        public ImmutableArray<BoundExpression> Initializers { get; }
    }
}