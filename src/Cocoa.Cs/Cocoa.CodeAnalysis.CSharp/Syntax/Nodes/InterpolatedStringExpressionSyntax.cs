using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>鎻掑€煎瓧绗︿覆琛ㄨ揪寮?<c>$"..."</c>锛氬瓧闈㈤噺鏂囨湰娈典笌鎻掑€兼礊锛?c>{expr}</c>锛変氦閿欍€?/summary>
    public sealed partial class InterpolatedStringExpressionSyntax : ExpressionSyntax
    {
        internal InterpolatedStringExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken interpolatedToken, ImmutableArray<InterpolatedStringContentSyntax> contents)
            : base(syntaxTree)
        {
            InterpolatedToken = interpolatedToken;
            Contents = contents;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.InterpolatedStringExpression;

        public SyntaxToken InterpolatedToken { get; }
        public ImmutableArray<InterpolatedStringContentSyntax> Contents { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return InterpolatedToken;
            foreach (var child in Contents)
            {
                yield return child;
            }
        }
    }
}

