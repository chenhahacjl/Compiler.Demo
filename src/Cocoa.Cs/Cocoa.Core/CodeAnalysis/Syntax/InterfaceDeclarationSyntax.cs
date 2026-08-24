using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 接口定义节点：`public interface IEnumerable&lt;T&gt;: IBase where T: class { ... }`（成员为无方法体的函数签名与属性访问器）。
    /// </summary>
    public sealed partial class InterfaceDeclarationSyntax : MemberSyntax
    {
        internal InterfaceDeclarationSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers, SyntaxToken interfaceKeyword, SyntaxToken identifier, TypeParameterListSyntax? typeParameters, ImmutableArray<TypeClauseSyntax> baseTypes, ImmutableArray<WhereClauseSyntax> whereClauses, SyntaxToken openBraceToken, ImmutableArray<MemberSyntax> members, SyntaxToken closeBraceToken)
            : base(syntaxTree, modifiers)
        {
            InterfaceKeyword = interfaceKeyword;
            Identifier = identifier;
            TypeParameters = typeParameters;
            BaseTypes = baseTypes;
            WhereClauses = whereClauses;
            OpenBraceToken = openBraceToken;
            Members = members;
            CloseBraceToken = closeBraceToken;
        }

        public override SyntaxKind Kind => SyntaxKind.InterfaceDeclaration;

        public SyntaxToken InterfaceKeyword { get; }
        public SyntaxToken Identifier { get; }

        /// <summary>泛型类型参数列表 `&lt;T&gt;`（6e-M20；非泛型接口为 null）。</summary>
        public TypeParameterListSyntax? TypeParameters { get; }

        /// <summary>基接口列表（`interface IBird: IAnimal, IFlyable` 的 `: IAnimal, IFlyable`）。</summary>
        public ImmutableArray<TypeClauseSyntax> BaseTypes { get; }

        /// <summary>泛型约束子句列表（`where T: ...`，6e-M20）。</summary>
        public ImmutableArray<WhereClauseSyntax> WhereClauses { get; }

        public SyntaxToken OpenBraceToken { get; }
        public ImmutableArray<MemberSyntax> Members { get; }
        public SyntaxToken CloseBraceToken { get; }
    }
}
