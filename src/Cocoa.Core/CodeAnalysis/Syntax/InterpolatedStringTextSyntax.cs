namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>插值字符串中的字面量文本段（合成 StringToken，Value 为已处理转义后的文本）。</summary>
    public sealed partial class InterpolatedStringTextSyntax : InterpolatedStringContentSyntax
    {
        internal InterpolatedStringTextSyntax(SyntaxTree syntaxTree, SyntaxToken textToken)
            : base(syntaxTree)
        {
            TextToken = textToken;
        }

        public override SyntaxKind Kind => SyntaxKind.InterpolatedStringText;

        public SyntaxToken TextToken { get; }
    }
}
