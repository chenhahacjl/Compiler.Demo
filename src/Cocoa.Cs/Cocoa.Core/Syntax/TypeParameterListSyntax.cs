using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 泛型类型参数列表（6e-M20）：`&lt;T, U&gt;`。
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
    }
}
