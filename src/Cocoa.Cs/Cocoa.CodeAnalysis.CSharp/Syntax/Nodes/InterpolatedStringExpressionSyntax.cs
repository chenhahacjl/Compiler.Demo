using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>插值字符串表达式 <c>$&quot;...&quot;</c>：字面量文本段与插值洞（<c>{expr}</c>）交替。</summary>
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

