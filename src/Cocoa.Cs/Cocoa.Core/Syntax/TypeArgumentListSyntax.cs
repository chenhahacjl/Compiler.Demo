using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 娉涘瀷绫诲瀷瀹炲弬鍒楄〃锛?e-M20锛夛細`&lt;int, string&gt;`锛堣皟鐢?鎴愬憳璋冪敤/瀵硅薄鍒涘缓鐨勬樉寮忓疄鍙傦紝棣栫増浠呮樉寮忥級銆?
    /// </summary>
    public sealed partial class TypeArgumentListSyntax : SyntaxNode
    {
        internal TypeArgumentListSyntax(SyntaxTree syntaxTree, SyntaxToken lessThanToken, ImmutableArray<TypeClauseSyntax> arguments, SyntaxToken greaterThanToken)
            : base(syntaxTree)
        {
            LessThanToken = lessThanToken;
            Arguments = arguments;
            GreaterThanToken = greaterThanToken;
        }

        public override SyntaxKind Kind => SyntaxKind.TypeArgumentList;

        public SyntaxToken LessThanToken { get; }
        public ImmutableArray<TypeClauseSyntax> Arguments { get; }
        public SyntaxToken GreaterThanToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return LessThanToken;
            foreach (var child in Arguments)
            {
                yield return child;
            }
            yield return GreaterThanToken;
        }
    }
}
