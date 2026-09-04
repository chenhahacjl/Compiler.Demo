using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>switch 节基类：case 子句或 default 子句。</summary>
    public abstract partial class SwitchSectionSyntax : CSharpSyntaxNode
    {
        private protected SwitchSectionSyntax(SyntaxTree syntaxTree)
            : base(syntaxTree)
        {
        }
    }
}

