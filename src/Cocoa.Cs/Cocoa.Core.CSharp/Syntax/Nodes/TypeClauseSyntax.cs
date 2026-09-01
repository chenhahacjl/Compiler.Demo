using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    public partial class TypeClauseSyntax : CSharpSyntaxNode
    {
        internal TypeClauseSyntax(SyntaxTree syntaxTree, SyntaxToken? colonToken, SyntaxToken identifier)
            : base(syntaxTree)
        {
            ColonToken = colonToken;
            Identifier = identifier;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.TypeClause;

        public SyntaxToken? ColonToken { get; }
        public SyntaxToken Identifier { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            if (ColonToken != null)
            {
                yield return ColonToken;
            }
            yield return Identifier;
        }
    }
}

