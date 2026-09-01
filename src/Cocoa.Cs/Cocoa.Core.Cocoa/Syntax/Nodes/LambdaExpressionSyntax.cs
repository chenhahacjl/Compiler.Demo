using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// Lambda 琛ㄨ揪寮忥紙6e-M22 C2锛夛細`(x: int, y: int) =&gt; expr | { 鈥?}`銆乣() => expr`锛?
    /// `.cs` 鏂硅█杩藉姞鍏嶆嫭鍙峰崟鍙?`x => expr`锛圤penParenthesisToken 涓?null锛夈€?
    /// 缁戝畾鍦?C3/C4 鎺ュ叆鈥斺€擟2 闃舵 Binder 闂ㄧ鎶ユ槑纭瘖鏂€?
    /// </summary>
    public sealed partial class LambdaExpressionSyntax : ExpressionSyntax
    {
        internal LambdaExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken? openParenthesisToken, SeparatedSyntaxList<ParameterSyntax> parameters, SyntaxToken? closeParenthesisToken, bool hasExplicitParameterTypes, SyntaxToken arrowToken, CocoaSyntaxNode body)
            : base(syntaxTree)
        {
            OpenParenthesisToken = openParenthesisToken;
            Parameters = parameters;
            CloseParenthesisToken = closeParenthesisToken;
            HasExplicitParameterTypes = hasExplicitParameterTypes;
            ArrowToken = arrowToken;
            Body = body;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.LambdaExpression;

        /// <summary>鍙傛暟鍒楄〃寮€鎷彿锛沶ull = 鍏嶆嫭鍙峰崟鍙傚舰鎬侊紙浠?.cs锛夈€?/summary>
        public SyntaxToken? OpenParenthesisToken { get; }

        public SeparatedSyntaxList<ParameterSyntax> Parameters { get; }

        public SyntaxToken? CloseParenthesisToken { get; }

        /// <summary>鍙傛暟鏄惁鍏ㄩ儴鏄惧紡鏍囨敞绫诲瀷锛圕# 瑙勫垯锛氫换涓€鏄惧紡鍒欓』鍏ㄩ儴鏄惧紡锛夈€?/summary>
        public bool HasExplicitParameterTypes { get; }

        public SyntaxToken ArrowToken { get; }

        /// <summary>lambda 浣擄細琛ㄨ揪寮忔垨鍧楄鍙ャ€?/summary>
        /// <summary>lambda 体：表达式或块语句（语言根类型，Kind 返回 <see cref="CocoaSyntaxKind"/>）。</summary>
        public CocoaSyntaxNode Body { get; }

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

