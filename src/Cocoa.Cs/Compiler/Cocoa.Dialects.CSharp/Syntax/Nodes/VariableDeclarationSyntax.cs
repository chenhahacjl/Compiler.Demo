using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    public sealed partial class VariableDeclarationSyntax : StatementSyntax
    {
        internal VariableDeclarationSyntax(SyntaxTree syntaxTree, SyntaxToken? keyword, SyntaxToken identifier, TypeClauseSyntax? typeClause, SyntaxToken? equalsToken, ExpressionSyntax? initializer)
            : base(syntaxTree)
        {
            Keyword = keyword;
            Identifier = identifier;
            TypeClause = typeClause;
            EqualsToken = equalsToken;
            Initializer = initializer;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.VariableDeclaration;

        public SyntaxToken? Keyword { get; }
        public SyntaxToken Identifier { get; }
        public TypeClauseSyntax? TypeClause { get; }
        public SyntaxToken? EqualsToken { get; }
        public ExpressionSyntax? Initializer { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            if (Keyword != null)
            {
                yield return Keyword;
            }
            yield return Identifier;
            if (TypeClause != null)
            {
                yield return TypeClause;
            }
            if (EqualsToken != null)
            {
                yield return EqualsToken;
            }
            if (Initializer != null)
            {
                yield return Initializer;
            }
        }
    }
}

