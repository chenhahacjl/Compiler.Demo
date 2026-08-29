using System.Collections.Immutable;
using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 插值字符串的高 Bound（Y A2-F1）：保留文本段与"已绑定洞"结构，由共享规范化 pass
    /// （<see cref="Lowering.InterpolationNormalizer"/>）降至 <see cref="BoundFormatExpression"/> / string 拼接。
    /// Type 恒为 string；本节点仅存在于"绑定后 → 规范化前"，cod / 发射 / 求值均消费降后形态。
    /// </summary>
    public sealed class BoundInterpolatedStringExpression : BoundExpression
    {
        public BoundInterpolatedStringExpression(SyntaxNode syntax, ImmutableArray<BoundInterpolationItem> items)
            : base(syntax)
        {
            Items = items;
        }

        public override BoundNodeKind Kind => BoundNodeKind.InterpolatedStringExpression;
        public override TypeSymbol Type => TypeSymbol.String;

        public ImmutableArray<BoundInterpolationItem> Items { get; }
    }

    /// <summary>插值部件：文本段（IsHole=false，Value=string 字面量）或洞（IsHole=true，Value=已绑定表达式，必要时带对齐/格式）。</summary>
    public sealed class BoundInterpolationItem
    {
        public BoundInterpolationItem(BoundExpression value, bool isHole, int? width, string? format, SyntaxNode syntax)
        {
            Value = value;
            IsHole = isHole;
            Width = width;
            Format = format;
            Syntax = syntax;
        }

        public BoundExpression Value { get; }
        public bool IsHole { get; }
        public int? Width { get; }
        public string? Format { get; }
        public SyntaxNode Syntax { get; }
    }
}