namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>switch 节基类：case 子句或 default 子句。</summary>
    public abstract partial class SwitchSectionSyntax : SyntaxNode
    {
        private protected SwitchSectionSyntax(SyntaxTree syntaxTree)
            : base(syntaxTree)
        {
        }
    }
}
