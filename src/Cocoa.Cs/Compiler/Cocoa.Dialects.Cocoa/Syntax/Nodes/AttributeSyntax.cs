using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// attribute 声明节点：`[Name]` / `[Name("arg1", "arg2")]`（6e-M32）。
    /// 挂类声明前（<see cref="ClassDeclarationSyntax.Attributes"/>）；本节点自带开/闭方括号，
    /// 实参为字符串字面量 token 列表（逗号分隔 token 一同入列，消费方按 Kind 过滤）。
    /// Tier-1 仅编译器识别 `Facade`；通用属性类解析为 Tier-2。
    /// </summary>
    public sealed partial class AttributeSyntax : CocoaSyntaxNode
    {
        internal AttributeSyntax(SyntaxTree syntaxTree, SyntaxToken openBracketToken, SyntaxToken name, SyntaxToken? openParenthesisToken, ImmutableArray<SyntaxToken> arguments, SyntaxToken? closeParenthesisToken, SyntaxToken closeBracketToken)
            : base(syntaxTree)
        {
            OpenBracketToken = openBracketToken;
            Name = name;
            OpenParenthesisToken = openParenthesisToken;
            Arguments = arguments;
            CloseParenthesisToken = closeParenthesisToken;
            CloseBracketToken = closeBracketToken;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.Attribute;

        public SyntaxToken OpenBracketToken { get; }

        /// <summary>attribute 名（如 `Facade`）。</summary>
        public SyntaxToken Name { get; }

        public SyntaxToken? OpenParenthesisToken { get; }

        /// <summary>字符串实参 + 逗号分隔 token（如 `[Facade("System.IntPtr")]` → [StringToken]）。</summary>
        public ImmutableArray<SyntaxToken> Arguments { get; }

        public SyntaxToken? CloseParenthesisToken { get; }

        public SyntaxToken CloseBracketToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return OpenBracketToken;
            yield return Name;
            if (OpenParenthesisToken != null)
            {
                yield return OpenParenthesisToken;
            }
            foreach (var child in Arguments)
            {
                yield return child;
            }
            if (CloseParenthesisToken != null)
            {
                yield return CloseParenthesisToken;
            }
            yield return CloseBracketToken;
        }
    }
}