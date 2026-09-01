using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 琛ㄨ揪寮忚娉?
    /// </summary>
    public abstract class ExpressionSyntax : CocoaSyntaxNode
    {
        private protected ExpressionSyntax(SyntaxTree syntaxTree)
            : base(syntaxTree)
        {
        }
    }
}

