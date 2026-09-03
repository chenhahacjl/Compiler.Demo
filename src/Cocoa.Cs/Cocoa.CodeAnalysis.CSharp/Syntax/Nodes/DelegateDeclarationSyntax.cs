using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// delegate 澹版槑锛?e-M22锛夛細`.co` `delegate void H(Object,string)` / `.cs` `public delegate void H(object,string);`
    /// Binder 鍚堟垚涓?sealed class extends MulticastDelegate + Invoke 鏂规硶锛堝鐢ㄥ叏閮ㄧ被鏈哄埗锛夈€?
    /// 婧愬簭鍖栫豢寰€杩旓紙P0锛夛細绾㈣妭鐐逛繚鐣?`(`/`)`锛堝強 `.cs` 鍒嗗彿锛夛紝鐪佺暐杩斿洖绫诲瀷锛?co 闅愬惈 void锛夋椂
    /// <see cref="ReturnType"/> 涓?null锛涙娊璞¤娉曟爲褰㈡€佺粡 <see cref="ReturnType.ColonToken"/> 鍒ゅ埆
    /// 锛堟湁鍐掑彿 = .co 鍚庣疆杩斿洖绫诲瀷锛屾棤鍐掑彿 = .cs 鍓嶇疆杩斿洖绫诲瀷锛夈€?
    /// </summary>
    public sealed partial class DelegateDeclarationSyntax : MemberSyntax
    {
        internal DelegateDeclarationSyntax(SyntaxTree syntaxTree, ImmutableArray<SyntaxToken> modifiers, SyntaxToken delegateKeyword, TypeClauseSyntax? returnType, SyntaxToken identifier, SyntaxToken openParenToken, SeparatedSyntaxList<ParameterSyntax> parameters, SyntaxToken closeParenToken, SyntaxToken? semicolonToken)
            : base(syntaxTree, modifiers)
        {
            DelegateKeyword = delegateKeyword;
            ReturnType = returnType;
            Identifier = identifier;
            OpenParenToken = openParenToken;
            Parameters = parameters;
            CloseParenToken = closeParenToken;
            SemicolonToken = semicolonToken;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.DelegateDeclaration;

        public SyntaxToken DelegateKeyword { get; }

        /// <summary>杩斿洖绫诲瀷锛堝彲绌猴細`.co` 闅愬惈 void 鏃剁渷鐣ワ級銆?/summary>
        public TypeClauseSyntax? ReturnType { get; }

        public SyntaxToken Identifier { get; }

        public SyntaxToken OpenParenToken { get; }

        public SeparatedSyntaxList<ParameterSyntax> Parameters { get; }

        public SyntaxToken CloseParenToken { get; }

        /// <summary>`.cs` 缁撳熬鍒嗗彿锛坄.co` 鏃狅級銆?/summary>
        public SyntaxToken? SemicolonToken { get; }

        /// <summary>鏄惁涓?C# 鍓嶇疆杩斿洖绫诲瀷褰㈡€侊紙`delegate int H(...)`锛?co 褰㈡€佽繑鍥炵被鍨嬪啋鍙峰悗缃垨鐪佺暐锛夈€?/summary>
        private bool IsCStyle => ReturnType != null && ReturnType.ColonToken == null;

        /// <summary>绾⑩啋缁挎簮搴忓寲锛氫繚璇?`GreenRoot.ToString() == 婧愮爜` 瀵?`.cs`/`.co` 涓ゅ舰鎬佹垚绔?
        /// 锛?co锛歞elegate 鍚?( 鍙傛暟 ) [: 杩斿洖绫诲瀷]锛?cs锛歞elegate 杩斿洖绫诲瀷 鍚?( 鍙傛暟 ) ;锛夈€?/summary>
        public override GreenNode ToGreen()
        {
            var slots = ImmutableArray.CreateBuilder<GreenNode?>();

            foreach (var modifier in Modifiers)
            {
                slots.Add(modifier.ToGreen());
            }

            slots.Add(DelegateKeyword.ToGreen());

            if (IsCStyle)
            {
                slots.Add(ReturnType!.ToGreen());
            }

            slots.Add(Identifier.ToGreen());
            slots.Add(OpenParenToken.ToGreen());

            foreach (var node in Parameters.GetWithSeparators())
            {
                slots.Add(node.ToGreen());
            }

            slots.Add(CloseParenToken.ToGreen());

            if (!IsCStyle && ReturnType != null)
            {
                slots.Add(ReturnType.ToGreen());
            }

            if (SemicolonToken != null)
            {
                slots.Add(SemicolonToken.ToGreen());
            }

            return new GreenNodeWithChildren((SyntaxKind)Kind, slots.ToImmutable());
        }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            foreach (var child in Modifiers)
            {
                yield return child;
            }
            yield return DelegateKeyword;
            if (ReturnType != null)
            {
                yield return ReturnType;
            }
            yield return Identifier;
            yield return OpenParenToken;
            foreach (var child in Parameters.GetWithSeparators())
            {
                yield return child;
            }
            yield return CloseParenToken;
            if (SemicolonToken != null)
            {
                yield return SemicolonToken;
            }
        }
    }
}

