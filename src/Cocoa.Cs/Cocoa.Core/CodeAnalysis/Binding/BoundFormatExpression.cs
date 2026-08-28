using Cocoa.CodeAnalysis.Symbols;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Binding
{
    /// <summary>
    /// 插值洞的格式化：<c>{expr[, width][: format]}</c>。Value 为原始值（保留类型供各后端按类型格式化），
    /// Type 恒为 string；宽度为对齐（负 = 左对齐），格式为说明符。仅对齐/格式存在时生成。
    /// </summary>
    public sealed class BoundFormatExpression : BoundExpression
    {
        public BoundFormatExpression(SyntaxNode syntax, BoundExpression value, int? width, string? format)
            : base(syntax)
        {
            Value = value;
            Width = width;
            Format = format;
        }

        public override BoundNodeKind Kind => BoundNodeKind.FormatExpression;
        public override TypeSymbol Type => TypeSymbol.String;

        public BoundExpression Value { get; }
        public int? Width { get; }
        public string? Format { get; }
    }
}
