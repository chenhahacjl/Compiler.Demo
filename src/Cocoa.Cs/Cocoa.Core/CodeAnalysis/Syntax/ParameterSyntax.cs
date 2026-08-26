namespace Cocoa.CodeAnalysis.Syntax
{
    public sealed partial class ParameterSyntax : SyntaxNode
    {
        internal ParameterSyntax(SyntaxTree syntaxTree, SyntaxToken identifier, TypeClauseSyntax type)
            : this(syntaxTree, modifier: null, identifier, type)
        {
        }

        internal ParameterSyntax(SyntaxTree syntaxTree, SyntaxToken? modifier, SyntaxToken identifier, TypeClauseSyntax type)
            : base(syntaxTree)
        {
            Modifier = modifier;
            Identifier = identifier;
            Type = type;
        }

        public override SyntaxKind Kind => SyntaxKind.Parameter;

        public SyntaxToken? Modifier { get; }
        public bool IsByRef => Modifier != null;

        public SyntaxToken Identifier { get; }
        public TypeClauseSyntax Type { get; }
    }
}
