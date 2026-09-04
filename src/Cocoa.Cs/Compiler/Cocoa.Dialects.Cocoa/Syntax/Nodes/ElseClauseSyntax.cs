using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    public sealed partial class ElseClauseSyntax : CocoaSyntaxNode
    {
        internal ElseClauseSyntax(SyntaxTree syntaxTree, SyntaxToken elseKeyword, StatementSyntax elseStatement)
            : base(syntaxTree)
        {
            ElseKeyword = elseKeyword;
            ElseStatement = elseStatement;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.ElseClause;

        public SyntaxToken ElseKeyword { get; }
        public StatementSyntax ElseStatement { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return ElseKeyword;
            yield return ElseStatement;
        }
    }
}

