using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 接口定义节点：`public interface IShape: IBase { ... }`（成员为无方法体的函数签名与属性访问器）。
    /// </summary>
    public sealed partial class InterfaceDeclarationSyntax : MemberSyntax
    {
        internal InterfaceDeclarationSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers, SyntaxToken interfaceKeyword, SyntaxToken identifier, ImmutableArray<TypeClauseSyntax> baseTypes, SyntaxToken openBraceToken, ImmutableArray<MemberSyntax> members, SyntaxToken closeBraceToken)
            : base(syntaxTree, modifiers)
        {
            InterfaceKeyword = interfaceKeyword;
            Identifier = identifier;
            BaseTypes = baseTypes;
            OpenBraceToken = openBraceToken;
            Members = members;
            CloseBraceToken = closeBraceToken;
        }

        public override SyntaxKind Kind => SyntaxKind.InterfaceDeclaration;

        public SyntaxToken InterfaceKeyword { get; }
        public SyntaxToken Identifier { get; }

        /// <summary>基接口列表（`interface IBird: IAnimal, IFlyable` 的 `: IAnimal, IFlyable`）。</summary>
        public ImmutableArray<TypeClauseSyntax> BaseTypes { get; }

        public SyntaxToken OpenBraceToken { get; }
        public ImmutableArray<MemberSyntax> Members { get; }
        public SyntaxToken CloseBraceToken { get; }
    }
}
