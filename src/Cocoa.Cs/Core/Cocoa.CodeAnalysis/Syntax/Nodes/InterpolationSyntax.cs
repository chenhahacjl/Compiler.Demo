using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Green;

namespace Cocoa.CodeAnalysis.Syntax.Nodes
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

        /// <summary><c>,</c>（对齐引导符）。/summary>
        public SyntaxToken? CommaToken { get; }

        /// <summary>对齐宽度（有符号整数字面量）。</summary>
        public ExpressionSyntax? Alignment { get; }

        /// <summary><c>:</c>（格式引导符）。/summary>
        public SyntaxToken? ColonToken { get; }

        /// <summary>格式说明符（字符串字面量）。/summary>
        public SyntaxToken? FormatToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Expression;
            if (CommaToken != null)
            {
                yield return CommaToken;
            }
            if (Alignment != null)
            {
                yield return Alignment;
            }
            if (ColonToken != null)
            {
                yield return ColonToken;
            }
            if (FormatToken != null)
            {
                yield return FormatToken;
            }
        }
    }
}

