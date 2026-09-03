using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// delegate 声明（6e-M22）：`.co` `delegate void H(Object,string)` / `.cs` `public delegate void H(object,string);`
    /// Binder 合成为 sealed class extends MulticastDelegate + Invoke 方法（复用全部类机制）。
    /// 源序化绿往返（P0）：红节点保留 `(`/`)`（及 `.cs` 分号），省略返回类型（.co 隐含 void）时
    /// <see cref="ReturnType"/> 为 null；抽象语法树形态经 <see cref="ReturnType.ColonToken"/> 判别
    /// （有冒号 = .co 后置返回类型，无冒号 = .cs 前置返回类型）。
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

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.DelegateDeclaration;

        public SyntaxToken DelegateKeyword { get; }

        /// <summary>返回类型（可空：`.co` 隐含 void 时省略）。</summary>
        public TypeClauseSyntax? ReturnType { get; }

        public SyntaxToken Identifier { get; }

        public SyntaxToken OpenParenToken { get; }

        public SeparatedSyntaxList<ParameterSyntax> Parameters { get; }

        public SyntaxToken CloseParenToken { get; }

        /// <summary>`.cs` 结尾分号（`.co` 无）。</summary>
        public SyntaxToken? SemicolonToken { get; }

        /// <summary>是否为 C# 前置返回类型形态（`delegate int H(...)`；.co 形态返回类型冒号后置或省略）。</summary>
        private bool IsCStyle => ReturnType != null && ReturnType.ColonToken == null;

        /// <summary>红→绿源序化：保证 `GreenRoot.ToString() == 源码` 对 `.cs`/`.co` 两形态成立。
        /// （.co：delegate 名（参数）[: 返回类型]；.cs：delegate 返回类型 名（参数）;）。</summary>
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

