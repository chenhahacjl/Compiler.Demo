using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 类字段声明节点：`private _x: int`（或 C# 系 `private int _x;`，可带初始化器 `= expr`）。
    /// </summary>
    public sealed partial class ClassFieldDeclarationSyntax : MemberSyntax
    {
        internal ClassFieldDeclarationSyntax(SyntaxTree syntaxTree, ImmutableArray<AttributeSyntax> attributes, ImmutableArray<SyntaxToken> modifiers, SyntaxToken identifier, TypeClauseSyntax type, SyntaxToken? equalsToken = null, ExpressionSyntax? initializer = null)
            : base(syntaxTree, modifiers)
        {
            Attributes = attributes;
            Identifier = identifier;
            Type = type;
            EqualsToken = equalsToken;
            Initializer = initializer;
        }

        public override SyntaxKind Kind => SyntaxKind.ClassFieldDeclaration;

        /// <summary>声明前 attribute 列表（6e-M32 Tier-2：字段级）。</summary>
        public ImmutableArray<AttributeSyntax> Attributes { get; }

        public SyntaxToken Identifier { get; }
        public TypeClauseSyntax Type { get; }
        public SyntaxToken? EqualsToken { get; }
        public ExpressionSyntax? Initializer { get; }

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
            yield return Identifier;
            yield return Type;
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

