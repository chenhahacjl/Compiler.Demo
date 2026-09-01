using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    public abstract class StatementSyntax : CocoaSyntaxNode
    {
        private protected StatementSyntax(SyntaxTree syntaxTree)
            : base(syntaxTree)
        {
        }
    }
}

