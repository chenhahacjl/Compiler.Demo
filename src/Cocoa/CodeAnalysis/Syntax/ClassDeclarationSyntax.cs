using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 类定义节点：`public class Foo: Bar { ... }`
    /// </summary>
    public sealed partial class ClassDeclarationSyntax : MemberSyntax
    {
        internal ClassDeclarationSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers, SyntaxToken classKeyword, SyntaxToken identifier, TypeClauseSyntax? baseType, SyntaxToken openBraceToken, ImmutableArray<MemberSyntax> members, SyntaxToken closeBraceToken)
            : base(syntaxTree, modifiers)
        {
            ClassKeyword = classKeyword;
            Identifier = identifier;
            BaseType = baseType;
            OpenBraceToken = openBraceToken;
            Members = members;
            CloseBraceToken = closeBraceToken;
        }

        public override SyntaxKind Kind => SyntaxKind.ClassDeclaration;

        public SyntaxToken ClassKeyword { get; }
        public SyntaxToken Identifier { get; }

        /// <summary>基类（`class Foo: Bar` 的 `: Bar`；null = 无显式基类）。</summary>
        public TypeClauseSyntax? BaseType { get; }

        public SyntaxToken OpenBraceToken { get; }
        public ImmutableArray<MemberSyntax> Members { get; }
        public SyntaxToken CloseBraceToken { get; }
    }
}
