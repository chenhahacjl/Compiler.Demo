using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 灞炴€у０鏄庯細`public property Name: string { get {...} set {...} }`锛堟垨鑷姩 `{ get; set }`锛夈€?
    /// </summary>
    public sealed partial class PropertyDeclarationSyntax : MemberSyntax
    {
        internal PropertyDeclarationSyntax(SyntaxTree syntaxTree, ImmutableArray<AttributeSyntax> attributes, ImmutableArray<SyntaxToken> modifiers, SyntaxToken? propertyKeyword, SyntaxToken identifier, TypeClauseSyntax type, SyntaxToken openBraceToken, PropertyAccessorSyntax? getter, PropertyAccessorSyntax? setter, SyntaxToken closeBraceToken, ImmutableArray<ParameterSyntax> parameters = default, SyntaxToken? equalsToken = null, ExpressionSyntax? initializer = null)
            : base(syntaxTree, modifiers)
        {
            Attributes = attributes;
            PropertyKeyword = propertyKeyword;
            Identifier = identifier;
            Type = type;
            OpenBraceToken = openBraceToken;
            Getter = getter;
            Setter = setter;
            CloseBraceToken = closeBraceToken;
            Parameters = parameters.IsDefault ? ImmutableArray<ParameterSyntax>.Empty : parameters;
            EqualsToken = equalsToken;
            Initializer = initializer;
        }

        public override SyntaxKind Kind => SyntaxKind.PropertyDeclaration;

        /// <summary>声明前 attribute 列表（6e-M32 Tier-2：属性级）。</summary>
        public ImmutableArray<AttributeSyntax> Attributes { get; }

        public SyntaxToken? PropertyKeyword { get; }
        public SyntaxToken Identifier { get; }
        public TypeClauseSyntax Type { get; }
        public SyntaxToken OpenBraceToken { get; }
        public PropertyAccessorSyntax? Getter { get; }
        public PropertyAccessorSyntax? Setter { get; }
        public SyntaxToken CloseBraceToken { get; }
        public ImmutableArray<ParameterSyntax> Parameters { get; }
        public SyntaxToken? EqualsToken { get; }
        public ExpressionSyntax? Initializer { get; }

        public bool IsAuto => Getter?.Body == null && Setter?.Body == null;

        public bool HasInitializer => Initializer != null;

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            foreach (var attribute in Attributes)
            {
                yield return attribute;
            }
            foreach (var child in Modifiers)
            {
                yield return child;
            }
            if (PropertyKeyword != null)
            {
                yield return PropertyKeyword;
            }
            yield return Identifier;
            yield return Type;
            yield return OpenBraceToken;
            if (Getter != null)
            {
                yield return Getter;
            }
            if (Setter != null)
            {
                yield return Setter;
            }
            yield return CloseBraceToken;
            foreach (var child in Parameters)
            {
                yield return child;
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

