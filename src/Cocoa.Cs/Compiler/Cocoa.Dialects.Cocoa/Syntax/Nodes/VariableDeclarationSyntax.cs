using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    public sealed partial class VariableDeclarationSyntax : StatementSyntax
    {
        internal VariableDeclarationSyntax(SyntaxTree syntaxTree, SyntaxToken? keyword, SyntaxToken identifier, TypeClauseSyntax? typeClause, SyntaxToken? equalsToken, ExpressionSyntax? initializer)
            : this(syntaxTree, null, keyword, identifier, typeClause, equalsToken, initializer) { }

        internal VariableDeclarationSyntax(SyntaxTree syntaxTree, SyntaxToken? usingKeyword, SyntaxToken? keyword, SyntaxToken identifier, TypeClauseSyntax? typeClause, SyntaxToken? equalsToken, ExpressionSyntax? initializer)
            : base(syntaxTree)
        {
            UsingKeyword = usingKeyword;
            Keyword = keyword;
            Identifier = identifier;
            TypeClause = typeClause;
            EqualsToken = equalsToken;
            Initializer = initializer;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.VariableDeclaration;

        public SyntaxToken? UsingKeyword { get; }
        public SyntaxToken? Keyword { get; }
        public SyntaxToken Identifier { get; }
        public TypeClauseSyntax? TypeClause { get; }
        public SyntaxToken? EqualsToken { get; }
        public ExpressionSyntax? Initializer { get; }

        public bool IsUsingDeclaration => UsingKeyword != null;

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            if (UsingKeyword != null) yield return UsingKeyword;
            if (Keyword != null) yield return Keyword;
            yield return Identifier;
            if (TypeClause != null) yield return TypeClause;
            if (EqualsToken != null) yield return EqualsToken;
            if (Initializer != null) yield return Initializer;
        }
    }
}

