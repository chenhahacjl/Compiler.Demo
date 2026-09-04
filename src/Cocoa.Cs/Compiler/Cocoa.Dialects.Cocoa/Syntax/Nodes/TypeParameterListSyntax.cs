using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    /// <summary>
    /// 泛型类型参数列表（6e-M20）：`&lt;T, U&gt;`。
    /// </summary>
    public sealed partial class TypeParameterListSyntax : CocoaSyntaxNode
    {
        internal TypeParameterListSyntax(SyntaxTree syntaxTree, SyntaxToken lessThanToken, ImmutableArray<SyntaxToken> parameters, SyntaxToken greaterThanToken)
            : base(syntaxTree)
        {
            LessThanToken = lessThanToken;
            Parameters = parameters;
            GreaterThanToken = greaterThanToken;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.TypeParameterList;

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

