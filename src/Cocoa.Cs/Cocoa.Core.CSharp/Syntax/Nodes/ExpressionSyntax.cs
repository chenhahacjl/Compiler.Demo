using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 琛ㄨ揪寮忚娉?
    /// </summary>
    public abstract class ExpressionSyntax : CSharpSyntaxNode
    {
        private protected ExpressionSyntax(SyntaxTree syntaxTree)
            : base(syntaxTree)
        {
        }
    }
}

