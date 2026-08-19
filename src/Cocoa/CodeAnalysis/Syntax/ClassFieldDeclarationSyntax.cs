using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 类字段声明节点：`private _x: int`
    /// </summary>
    public sealed partial class ClassFieldDeclarationSyntax : MemberSyntax
    {
        internal ClassFieldDeclarationSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers, SyntaxToken identifier, TypeClauseSyntax type)
            : base(syntaxTree, modifiers)
        {
            Identifier = identifier;
            Type = type;
        }

        public override SyntaxKind Kind => SyntaxKind.ClassFieldDeclaration;

        public SyntaxToken Identifier { get; }
        public TypeClauseSyntax Type { get; }
    }
}
