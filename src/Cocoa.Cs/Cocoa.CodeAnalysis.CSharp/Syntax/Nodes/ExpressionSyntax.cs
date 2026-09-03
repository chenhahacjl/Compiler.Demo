using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 表达式语法
    /// </summary>
    public abstract class ExpressionSyntax : CSharpSyntaxNode
    {
        private protected ExpressionSyntax(SyntaxTree syntaxTree)
            : base(syntaxTree)
        {
        }
    }
}

