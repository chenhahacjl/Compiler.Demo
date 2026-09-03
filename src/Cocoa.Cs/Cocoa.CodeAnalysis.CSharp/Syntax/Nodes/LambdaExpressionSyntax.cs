using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// Lambda 表达式（6e-M22 C2）：`(x: int, y: int) =&gt; expr | {  }`、`() => expr`＀
    /// `.cs` 鏂硅█杩藉姞鍏嶆嫭鍙峰崟鍙?`x => expr`锛圤penParenthesisToken 涓?null锛夈€?
    /// 绑定圀C3/C4 接入——C2 阶段 Binder 门禁报明确诊断、
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

        /// <summary>鍙傛暟鍒楄〃寮€鎷彿锛沶ull = 鍏嶆嫭鍙峰崟鍙傚舰鎬侊紙浠?.cs锛夈€?/summary>
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

