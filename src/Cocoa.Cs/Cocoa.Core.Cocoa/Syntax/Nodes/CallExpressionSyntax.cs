using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 鍑芥暟璋冪敤琛ㄨ揪寮忚娉曪細`F(args)` / 娉涘瀷鏄惧紡瀹炲弬 `Swap&lt;int&gt;(a, b)`锛?e-M20锛夈€?
    /// </summary>
    public sealed partial class CallExpressionSyntax : ExpressionSyntax
    {
        internal CallExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken identifier, TypeArgumentListSyntax? typeArguments, SyntaxToken openParenthesisToken, SeparatedSyntaxList<ExpressionSyntax> arguments, SyntaxToken closeParenthesisToken)
            : base(syntaxTree)
        {
            Identifier = identifier;
            TypeArguments = typeArguments;
            OpenParenthesisToken = openParenthesisToken;
            Arguments = arguments;
            CloseParenthesisToken = closeParenthesisToken;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.CallExpression;

        public SyntaxToken Identifier { get; }

        /// <summary>娉涘瀷绫诲瀷瀹炲弬鍒楄〃锛坄Swap<int>(鈥?`锛涢潪娉涘瀷璋冪敤涓?null锛?e-M20 棣栫増浠呮樉寮忓疄鍙傦級銆?/summary>
        public TypeArgumentListSyntax? TypeArguments { get; }

        public SyntaxToken OpenParenthesisToken { get; }
        public SeparatedSyntaxList<ExpressionSyntax> Arguments { get; }
        public SyntaxToken CloseParenthesisToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Identifier;
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

