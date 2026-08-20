using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 属性声明：`public property Name: string { get {...} set {...} }`（或自动 `{ get; set }`）。
    /// </summary>
    public sealed partial class PropertyDeclarationSyntax : MemberSyntax
    {
        internal PropertyDeclarationSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers, SyntaxToken? propertyKeyword, SyntaxToken identifier, TypeClauseSyntax type, SyntaxToken openBraceToken, PropertyAccessorSyntax? getter, PropertyAccessorSyntax? setter, SyntaxToken closeBraceToken, SyntaxToken? equalsToken = null, ExpressionSyntax? initializer = null)
            : base(syntaxTree, modifiers)
        {
            PropertyKeyword = propertyKeyword;
            Identifier = identifier;
            Type = type;
            OpenBraceToken = openBraceToken;
            Getter = getter;
            Setter = setter;
            CloseBraceToken = closeBraceToken;
            EqualsToken = equalsToken;
            Initializer = initializer;
        }

        public override SyntaxKind Kind => SyntaxKind.PropertyDeclaration;

        public SyntaxToken? PropertyKeyword { get; }
        public SyntaxToken Identifier { get; }
        public TypeClauseSyntax Type { get; }
        public SyntaxToken OpenBraceToken { get; }
        public PropertyAccessorSyntax? Getter { get; }
        public PropertyAccessorSyntax? Setter { get; }
        public SyntaxToken CloseBraceToken { get; }
        public SyntaxToken? EqualsToken { get; }
        public ExpressionSyntax? Initializer { get; }

        public bool IsAuto => Getter?.Body == null && Setter?.Body == null;

        public bool HasInitializer => Initializer != null;
    }
}
