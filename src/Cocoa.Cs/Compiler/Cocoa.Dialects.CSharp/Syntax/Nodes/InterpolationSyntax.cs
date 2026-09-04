using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
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

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.Interpolation;

        public ExpressionSyntax Expression { get; }

        /// <summary><c>,</c>锛堝榻愬紩瀵肩锛夈€?/summary>
        public SyntaxToken? CommaToken { get; }

        /// <summary>对齐宽度（有符号整数字面量）。</summary>
        public ExpressionSyntax? Alignment { get; }

        /// <summary><c>:</c>锛堟牸寮忓紩瀵肩锛夈€?/summary>
        public SyntaxToken? ColonToken { get; }

        /// <summary>鏍煎紡璇存槑绗︼紙瀛楃涓插瓧闈㈤噺锛夈€?/summary>
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

