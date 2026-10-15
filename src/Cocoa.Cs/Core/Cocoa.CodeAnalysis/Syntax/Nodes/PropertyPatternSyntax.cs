using System.Collections.Generic;
using System.Collections.Immutable;
using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 属性模式：expr is { Length: > 0 } / expr is { Name: "hello", Age: > 18 }
    /// 检查对象属性是否匹配指定的子模式
    /// </summary>
    public sealed class PropertyPatternSyntax : PatternSyntax
    {
        public PropertyPatternSyntax(SyntaxTree syntaxTree, SyntaxToken openBraceToken, ImmutableArray<PropertySubpatternSyntax> subpatterns, SyntaxToken closeBraceToken)
            : base(syntaxTree)
        {
            OpenBraceToken = openBraceToken;
            Subpatterns = subpatterns;
            CloseBraceToken = closeBraceToken;
        }

        public override SyntaxKind Kind => SyntaxKind.PropertyPattern;

        public SyntaxToken OpenBraceToken { get; }
        public ImmutableArray<PropertySubpatternSyntax> Subpatterns { get; }
        public SyntaxToken CloseBraceToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return OpenBraceToken;
            foreach (var sub in Subpatterns)
                yield return sub;
            yield return CloseBraceToken;
        }
    }
}
