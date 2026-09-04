using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    public abstract class StatementSyntax : CSharpSyntaxNode
    {
        private protected StatementSyntax(SyntaxTree syntaxTree)
            : base(syntaxTree)
        {
        }
    }
}

