using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// 瀵硅薄鍒涘缓琛ㄨ揪寮忥細`new Foo(args)` / 娉涘瀷 `new List&lt;int&gt;(args)`锛?e-M20锛夈€?
    /// </summary>
    public sealed partial class ObjectCreationExpressionSyntax : ExpressionSyntax
    {
        internal ObjectCreationExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken newKeyword, SyntaxToken identifier, TypeArgumentListSyntax? typeArguments, SyntaxToken openParenthesisToken, SeparatedSyntaxList<ExpressionSyntax> arguments, SyntaxToken closeParenthesisToken)
            : base(syntaxTree)
        {
            NewKeyword = newKeyword;
            Identifier = identifier;
            TypeArguments = typeArguments;
            OpenParenthesisToken = openParenthesisToken;
            Arguments = arguments;
            CloseParenthesisToken = closeParenthesisToken;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.ObjectCreationExpression;

        public SyntaxToken NewKeyword { get; }
        public SyntaxToken Identifier { get; }

        /// <summary>娉涘瀷绫诲瀷瀹炲弬鍒楄〃锛坄new List<int>(鈥?`锛涢潪娉涘瀷鍒涘缓涓?null锛?e-M20锛夈€?/summary>
        public TypeArgumentListSyntax? TypeArguments { get; }

        public SyntaxToken OpenParenthesisToken { get; }
        public SeparatedSyntaxList<ExpressionSyntax> Arguments { get; }
        public SyntaxToken CloseParenthesisToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return NewKeyword;
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

