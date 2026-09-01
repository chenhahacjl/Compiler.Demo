using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 娉涘瀷绫诲瀷鍙傛暟鍒楄〃锛?e-M20锛夛細`&lt;T, U&gt;`銆?
    /// </summary>
    public sealed partial class TypeParameterListSyntax : SyntaxNode
    {
        internal TypeParameterListSyntax(SyntaxTree syntaxTree, SyntaxToken lessThanToken, ImmutableArray<SyntaxToken> parameters, SyntaxToken greaterThanToken)
            : base(syntaxTree)
        {
            LessThanToken = lessThanToken;
            Parameters = parameters;
            GreaterThanToken = greaterThanToken;
        }

        public override SyntaxKind Kind => SyntaxKind.TypeParameterList;

        public SyntaxToken LessThanToken { get; }
        public ImmutableArray<SyntaxToken> Parameters { get; }
        public SyntaxToken GreaterThanToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return LessThanToken;
            foreach (var child in Parameters)
            {
                yield return child;
            }
            yield return GreaterThanToken;
        }
    }
}
