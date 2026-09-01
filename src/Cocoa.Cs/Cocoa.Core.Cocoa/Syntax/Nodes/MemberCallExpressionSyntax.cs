using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 鎴愬憳鏂规硶璋冪敤琛ㄨ揪寮忚娉曪細`s.substring(1, 3)` / 娉涘瀷鏄惧紡瀹炲弬 `list.Map&lt;int&gt;(f)`锛?e-M20锛夈€?
    /// </summary>
    public sealed partial class MemberCallExpressionSyntax : ExpressionSyntax
    {
        internal MemberCallExpressionSyntax(SyntaxTree syntaxTree, ExpressionSyntax expression, SyntaxToken dotToken, SyntaxToken identifierToken, TypeArgumentListSyntax? typeArguments, SyntaxToken openParenthesisToken, SeparatedSyntaxList<ExpressionSyntax> arguments, SyntaxToken closeParenthesisToken)
            : base(syntaxTree)
        {
            Expression = expression;
            DotToken = dotToken;
            IdentifierToken = identifierToken;
            TypeArguments = typeArguments;
            OpenParenthesisToken = openParenthesisToken;
            Arguments = arguments;
            CloseParenthesisToken = closeParenthesisToken;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.MemberCallExpression;

        public ExpressionSyntax Expression { get; }
        public SyntaxToken DotToken { get; }
        public SyntaxToken IdentifierToken { get; }

        /// <summary>娉涘瀷绫诲瀷瀹炲弬鍒楄〃锛坄obj.M<int>(鈥?`锛涢潪娉涘瀷璋冪敤涓?null锛?e-M20 棣栫増浠呮樉寮忓疄鍙傦級銆?/summary>
        public TypeArgumentListSyntax? TypeArguments { get; }

        public SyntaxToken OpenParenthesisToken { get; }
        public SeparatedSyntaxList<ExpressionSyntax> Arguments { get; }
        public SyntaxToken CloseParenthesisToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Expression;
            yield return DotToken;
            yield return IdentifierToken;
            if (TypeArguments != null)
            {
                yield return TypeArguments;
            }
            yield return OpenParenthesisToken;
            foreach (var child in Arguments.GetWithSeparators())
            {
                yield return child;
            }
            yield return CloseParenthesisToken;
        }
    }
}

