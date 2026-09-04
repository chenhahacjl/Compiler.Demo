using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    public sealed partial class ElseClauseSyntax : CSharpSyntaxNode
    {
        internal ElseClauseSyntax(SyntaxTree syntaxTree, SyntaxToken elseKeyword, StatementSyntax elseStatement)
            : base(syntaxTree)
        {
            ElseKeyword = elseKeyword;
            ElseStatement = elseStatement;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.ElseClause;

        public SyntaxToken ElseKeyword { get; }
        public StatementSyntax ElseStatement { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return ElseKeyword;
            yield return ElseStatement;
        }
    }
}

