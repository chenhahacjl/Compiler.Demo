using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    public sealed partial class CatchClauseSyntax : CSharpSyntaxNode
    {
        internal CatchClauseSyntax(SyntaxTree syntaxTree, SyntaxToken catchKeyword, SyntaxToken identifier, TypeClauseSyntax type, BlockStatementSyntax body)
            : base(syntaxTree)
        {
            CatchKeyword = catchKeyword;
            Identifier = identifier;
            Type = type;
            Body = body;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.CatchClause;

        public SyntaxToken CatchKeyword { get; }
        public SyntaxToken Identifier { get; }
        public TypeClauseSyntax Type { get; }
        public BlockStatementSyntax Body { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return CatchKeyword;
            yield return Identifier;
            yield return Type;
            yield return Body;
        }
    }
}

