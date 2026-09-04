using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// Lambda 表达式（6e-M22 C2）：`(x: int, y: int) =&gt; expr | { ... }`、`() => expr` 等。
    /// `.cs` 方言追加免括号单参 `x => expr`（OpenParenthesisToken 为 null）。
    /// 绑定期 C3/C4 接入——C2 阶段 Binder 门禁报明确诊断。
    /// </summary>
    public sealed partial class LambdaExpressionSyntax : ExpressionSyntax
    {
        internal LambdaExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken? openParenthesisToken, SeparatedSyntaxList<ParameterSyntax> parameters, SyntaxToken? closeParenthesisToken, bool hasExplicitParameterTypes, SyntaxToken arrowToken, CSharpSyntaxNode body)
            : base(syntaxTree)
        {
            OpenParenthesisToken = openParenthesisToken;
            Parameters = parameters;
            CloseParenthesisToken = closeParenthesisToken;
            HasExplicitParameterTypes = hasExplicitParameterTypes;
            ArrowToken = arrowToken;
            Body = body;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.LambdaExpression;

        /// <summary>参数列表开括号；null = 免括号单参形态（仅 .cs）。</summary>
        public SyntaxToken? OpenParenthesisToken { get; }

        public SeparatedSyntaxList<ParameterSyntax> Parameters { get; }

        public SyntaxToken? CloseParenthesisToken { get; }

        /// <summary>鍙傛暟鏄惁鍏ㄩ儴鏄惧紡鏍囨敞绫诲瀷锛圕# 瑙勫垯锛氫换涓€鏄惧紡鍒欓』鍏ㄩ儴鏄惧紡锛夈€?/summary>
        public bool HasExplicitParameterTypes { get; }

        public SyntaxToken ArrowToken { get; }

        /// <summary>lambda 浣擄細琛ㄨ揪寮忔垨鍧楄鍙ャ€?/summary>
        public CSharpSyntaxNode Body { get; }

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

