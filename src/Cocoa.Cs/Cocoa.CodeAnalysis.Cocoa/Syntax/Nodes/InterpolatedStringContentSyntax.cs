using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>插值字符串内容基类：字面量文本段或插值洞。</summary>
    public abstract partial class InterpolatedStringContentSyntax : CocoaSyntaxNode
    {
        private protected InterpolatedStringContentSyntax(SyntaxTree syntaxTree)
            : base(syntaxTree)
        {
        }
    }
}

