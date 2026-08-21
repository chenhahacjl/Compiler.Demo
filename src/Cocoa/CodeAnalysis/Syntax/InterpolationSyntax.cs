namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>插值洞 <c>{expr[, alignment][: format]}</c>。</summary>
    public sealed partial class InterpolationSyntax : InterpolatedStringContentSyntax
    {
        internal InterpolationSyntax(SyntaxTree syntaxTree, ExpressionSyntax expression, SyntaxToken? commaToken, ExpressionSyntax? alignment, SyntaxToken? colonToken, SyntaxToken? formatToken)
            : base(syntaxTree)
        {
            Expression = expression;
            CommaToken = commaToken;
            Alignment = alignment;
            ColonToken = colonToken;
            FormatToken = formatToken;
        }

        public override SyntaxKind Kind => SyntaxKind.Interpolation;

        public ExpressionSyntax Expression { get; }

        /// <summary><c>,</c>（对齐引导符）。</summary>
        public SyntaxToken? CommaToken { get; }

        /// <summary>对齐宽度（有符号整数字面量）。</summary>
        public ExpressionSyntax? Alignment { get; }

        /// <summary><c>:</c>（格式引导符）。</summary>
        public SyntaxToken? ColonToken { get; }

        /// <summary>格式说明符（字符串字面量）。</summary>
        public SyntaxToken? FormatToken { get; }
    }
}
