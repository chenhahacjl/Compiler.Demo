using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>鎻掑€煎瓧绗︿覆鍐呭鍩虹被锛氬瓧闈㈤噺鏂囨湰娈垫垨鎻掑€兼礊銆?/summary>
    public abstract partial class InterpolatedStringContentSyntax : CSharpSyntaxNode
    {
        private protected InterpolatedStringContentSyntax(SyntaxTree syntaxTree)
            : base(syntaxTree)
        {
        }
    }
}

