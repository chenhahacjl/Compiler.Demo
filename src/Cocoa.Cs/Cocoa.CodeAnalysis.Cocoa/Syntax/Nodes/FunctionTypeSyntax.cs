using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 函数类型（6e-M22 C2）：`(A, B) -&gt; R`（仅 `.co`；`.cs` 走 Func/Action/Predicate 家族）。
    /// 缁ф壙 TypeClauseSyntax 浠ユ棤缂濊繘鍏ュ叏閮ㄧ被鍨嬩綅缃紙鍙傛暟/杩斿洖/鍙橀噺/娉涘瀷瀹炲弬锛夆€斺€?
    /// 基类 Identifier 为合成缺失 token，消费方按 Kind == FunctionType 先行分流。
    /// </summary>
    public sealed partial class FunctionTypeSyntax : TypeClauseSyntax
    {
        internal FunctionTypeSyntax(SyntaxTree syntaxTree, SyntaxToken openParenthesisToken, SeparatedSyntaxList<TypeClauseSyntax> parameterTypes, SyntaxToken closeParenthesisToken, SyntaxToken arrowToken, TypeClauseSyntax returnType)
            : base(syntaxTree, colonToken: null, identifier: new SyntaxToken(syntaxTree, (SyntaxKind)CocoaSyntaxKind.IdentifierToken, syntaxTree.Text.Length, null, null, ImmutableArray<SyntaxTrivia>.Empty, ImmutableArray<SyntaxTrivia>.Empty))
        {
            OpenParenthesisToken = openParenthesisToken;
            ParameterTypes = parameterTypes;
            CloseParenthesisToken = closeParenthesisToken;
            ArrowToken = arrowToken;
            ReturnType = returnType;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.FunctionType;

        public SyntaxToken OpenParenthesisToken { get; }

        public SeparatedSyntaxList<TypeClauseSyntax> ParameterTypes { get; }

        public SyntaxToken CloseParenthesisToken { get; }

        public SyntaxToken ArrowToken { get; }

        public TypeClauseSyntax ReturnType { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            if (ColonToken != null)
            {
                yield return ColonToken;
            }
            yield return Identifier;
            yield return OpenParenthesisToken;
            foreach (var child in ParameterTypes.GetWithSeparators())
            {
                yield return child;
            }
            yield return CloseParenthesisToken;
            yield return ArrowToken;
            yield return ReturnType;
        }
    }
}


