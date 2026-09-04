using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 类定义节点：`public class Foo&lt;T&gt;: Bar, IA, IB where T: IComparable&lt;T&gt; { ... }`
    /// </summary>
    public sealed partial class ClassDeclarationSyntax : MemberSyntax
    {
        internal ClassDeclarationSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers, SyntaxToken classKeyword, SyntaxToken identifier, TypeParameterListSyntax? typeParameters, ImmutableArray<TypeClauseSyntax> baseTypes, ImmutableArray<WhereClauseSyntax> whereClauses, SyntaxToken openBraceToken, ImmutableArray<MemberSyntax> members, SyntaxToken closeBraceToken)
            : base(syntaxTree, modifiers)
        {
            ClassKeyword = classKeyword;
            Identifier = identifier;
            TypeParameters = typeParameters;
            BaseTypes = baseTypes;
            WhereClauses = whereClauses;
            OpenBraceToken = openBraceToken;
            Members = members;
            CloseBraceToken = closeBraceToken;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.ClassDeclaration;

        public SyntaxToken ClassKeyword { get; }
        public SyntaxToken Identifier { get; }

        /// <summary>是否 struct（值类型）：classKeyword 为 struct 关键字时成立（6e-M26）。</summary>
        public bool IsStruct => ClassKeyword.Kind == (SyntaxKind)CocoaSyntaxKind.StructKeyword;

        /// <summary>泛型类型参数列表 `&lt;T, U&gt;`（6e-M20；非泛型类为 null）。</summary>
        public TypeParameterListSyntax? TypeParameters { get; }

        /// <summary>基类型列表（`class Foo: Bar, IA, IB` 的 `: ...`；首个非接口 = 基类，其余须为接口）。</summary>
        public ImmutableArray<TypeClauseSyntax> BaseTypes { get; }

        /// <summary>泛型约束子句列表（`where T: ...`，6e-M20）。</summary>
        public ImmutableArray<WhereClauseSyntax> WhereClauses { get; }

        public SyntaxToken OpenBraceToken { get; }
        public ImmutableArray<MemberSyntax> Members { get; }
        public SyntaxToken CloseBraceToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            foreach (var child in Modifiers)
            {
                yield return child;
            }
            yield return ClassKeyword;
            yield return Identifier;
            if (TypeParameters != null)
            {
                yield return TypeParameters;
            }
            foreach (var child in BaseTypes)
            {
                yield return child;
            }
            foreach (var child in WhereClauses)
            {
                yield return child;
            }
            yield return OpenBraceToken;
            foreach (var child in Members)
            {
                yield return child;
            }
            yield return CloseBraceToken;
        }
    }
}


