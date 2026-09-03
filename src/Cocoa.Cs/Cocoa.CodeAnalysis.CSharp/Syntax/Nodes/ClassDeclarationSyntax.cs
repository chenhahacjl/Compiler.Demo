using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 绫诲畾涔夎妭鐐癸細`public class Foo&lt;T&gt;: Bar, IA, IB where T: IComparable&lt;T&gt; { ... }`
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

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.ClassDeclaration;

        public SyntaxToken ClassKeyword { get; }
        public SyntaxToken Identifier { get; }

        /// <summary>鏄惁 struct锛堝€肩被鍨嬶級锛歝lassKeyword 涓?struct 鍏抽敭瀛楁椂鎴愮珛锛?e-M26锛夈€?/summary>
        public bool IsStruct => ClassKeyword.Kind == (SyntaxKind)CSharpSyntaxKind.StructKeyword;

        /// <summary>娉涘瀷绫诲瀷鍙傛暟鍒楄〃 `&lt;T, U&gt;`锛?e-M20锛涢潪娉涘瀷绫讳负 null锛夈€?/summary>
        public TypeParameterListSyntax? TypeParameters { get; }

        /// <summary>鍩虹被鍨嬪垪琛紙`class Foo: Bar, IA, IB` 鐨?`: ...`锛涢涓潪鎺ュ彛 = 鍩虹被锛屽叾浣欓』涓烘帴鍙ｏ級銆?/summary>
        public ImmutableArray<TypeClauseSyntax> BaseTypes { get; }

        /// <summary>娉涘瀷绾︽潫瀛愬彞鍒楄〃锛坄where T: ...`锛?e-M20锛夈€?/summary>
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


