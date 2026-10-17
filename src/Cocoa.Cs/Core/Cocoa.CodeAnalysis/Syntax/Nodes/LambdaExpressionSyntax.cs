using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Green;

namespace Cocoa.CodeAnalysis.Syntax.Nodes
{
    /// <summary>
    /// Lambda 表达式（6e-M22 C2）：`(x: int, y: int) =&gt; expr | { ... }`、`() => expr` 等。
    /// 追加免括号单参 `x => expr`（OpenParenthesisToken 为 null）。
    /// 绑定期 C3/C4 接入——C2 阶段 Binder 门禁报明确诊断。
    /// </summary>
    public sealed partial class LambdaExpressionSyntax : ExpressionSyntax
    {
        internal LambdaExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken? openParenthesisToken, SeparatedSyntaxList<ParameterSyntax> parameters, SyntaxToken? closeParenthesisToken, bool hasExplicitParameterTypes, SyntaxToken arrowToken, SyntaxNode body)
            : base(syntaxTree)
        {
            OpenParenthesisToken = openParenthesisToken;
            Parameters = parameters;
            CloseParenthesisToken = closeParenthesisToken;
            HasExplicitParameterTypes = hasExplicitParameterTypes;
            ArrowToken = arrowToken;
            Body = body;
        }

        public override SyntaxKind Kind => SyntaxKind.LambdaExpression;

        /// <summary>参数列表开括号；null = 免括号单参形态（仅 .cs）。</summary>
        public SyntaxToken? OpenParenthesisToken { get; }

        public SeparatedSyntaxList<ParameterSyntax> Parameters { get; }

        public SyntaxToken? CloseParenthesisToken { get; }

        /// <summary>参数是否全部显式标注类型（C# 规则：任一显式则须全部显式）。/summary>
        public bool HasExplicitParameterTypes { get; }

        public SyntaxToken ArrowToken { get; }

        /// <summary>lambda 体：表达式或块语句。/summary>
        /// <summary>lambda 体：表达式或块语句（语言根类型，Kind 返回 <see cref="SyntaxKind"/>）。</summary>
        public SyntaxNode Body { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            if (OpenParenthesisToken != null)
            {
                yield return OpenParenthesisToken;
            }
            foreach (var child in Parameters.GetWithSeparators())
            {
                yield return child;
            }
            if (CloseParenthesisToken != null)
            {
                yield return CloseParenthesisToken;
            }
            yield return ArrowToken;
            yield return Body;
        }
    }
}

