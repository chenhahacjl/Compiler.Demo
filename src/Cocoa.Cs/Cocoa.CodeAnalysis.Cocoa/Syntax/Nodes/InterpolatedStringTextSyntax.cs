using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>插值字符串中的字面量文本段（合成 StringToken，Value 为已处理转义后的文本）。</summary>
    public sealed partial class InterpolatedStringTextSyntax : InterpolatedStringContentSyntax
    {
        internal InterpolatedStringTextSyntax(SyntaxTree syntaxTree, SyntaxToken textToken)
            : base(syntaxTree)
        {
            TextToken = textToken;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.InterpolatedStringText;

        public SyntaxToken TextToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return TextToken;
        }
    }
}

