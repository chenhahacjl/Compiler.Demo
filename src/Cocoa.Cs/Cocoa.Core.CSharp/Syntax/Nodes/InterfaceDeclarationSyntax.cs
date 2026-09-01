using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 鎺ュ彛瀹氫箟鑺傜偣锛歚public interface IEnumerable&lt;T&gt;: IBase where T: class { ... }`锛堟垚鍛樹负鏃犳柟娉曚綋鐨勫嚱鏁扮鍚嶄笌灞炴€ц闂櫒锛夈€?
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

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.InterfaceDeclaration;

        public SyntaxToken InterfaceKeyword { get; }
        public SyntaxToken Identifier { get; }

        /// <summary>娉涘瀷绫诲瀷鍙傛暟鍒楄〃 `&lt;T&gt;`锛?e-M20锛涢潪娉涘瀷鎺ュ彛涓?null锛夈€?/summary>
        public TypeParameterListSyntax? TypeParameters { get; }

        /// <summary>鍩烘帴鍙ｅ垪琛紙`interface IBird: IAnimal, IFlyable` 鐨?`: IAnimal, IFlyable`锛夈€?/summary>
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
            yield return InterfaceKeyword;
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

