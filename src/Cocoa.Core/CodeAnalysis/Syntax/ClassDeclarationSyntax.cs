using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 类定义节点：`public class Foo: Bar, IA, IB { ... }`
    /// </summary>
    public sealed partial class ClassDeclarationSyntax : MemberSyntax
    {
        internal ClassDeclarationSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers, SyntaxToken classKeyword, SyntaxToken identifier, ImmutableArray<TypeClauseSyntax> baseTypes, SyntaxToken openBraceToken, ImmutableArray<MemberSyntax> members, SyntaxToken closeBraceToken)
            : base(syntaxTree, modifiers)
        {
            ClassKeyword = classKeyword;
            Identifier = identifier;
            BaseTypes = baseTypes;
            OpenBraceToken = openBraceToken;
            Members = members;
            CloseBraceToken = closeBraceToken;
        }

        public override SyntaxKind Kind => SyntaxKind.ClassDeclaration;

        public SyntaxToken ClassKeyword { get; }
        public SyntaxToken Identifier { get; }

        /// <summary>基类型列表（`class Foo: Bar, IA, IB` 的 `: ...`；首个非接口 = 基类，其余须为接口）。</summary>
        public ImmutableArray<TypeClauseSyntax> BaseTypes { get; }

        public SyntaxToken OpenBraceToken { get; }
        public ImmutableArray<MemberSyntax> Members { get; }
        public SyntaxToken CloseBraceToken { get; }
    }
}
