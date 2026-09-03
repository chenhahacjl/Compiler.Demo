using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 鍑芥暟绫诲瀷锛?e-M22 C2锛夛細`(A, B) -&gt; R`锛堜粎 `.co`锛沗.cs` 璧?Func/Action/Predicate 瀹舵棌锛夈€?
    /// 缁ф壙 TypeClauseSyntax 浠ユ棤缂濊繘鍏ュ叏閮ㄧ被鍨嬩綅缃紙鍙傛暟/杩斿洖/鍙橀噺/娉涘瀷瀹炲弬锛夆€斺€?
    /// 鍩虹被 Identifier 涓哄悎鎴愮己澶?token锛屾秷璐规柟鎸?Kind == FunctionType 鍏堣鍒嗘祦銆?
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


