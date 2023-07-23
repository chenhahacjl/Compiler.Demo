namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 表达式语法
    /// </summary>
    public abstract class ExpressionSyntax : SyntaxNode
    {
        protected ExpressionSyntax(SyntaxTree syntaxTree)
            : base(syntaxTree)
        {
        }
    }
}
