using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>插值字符串表达式 <c>$"..."</c>：字面量文本段与插值洞（<c>{expr}</c>）交错。</summary>
    public sealed partial class InterpolatedStringExpressionSyntax : ExpressionSyntax
    {
        internal InterpolatedStringExpressionSyntax(SyntaxTree syntaxTree, SyntaxToken interpolatedToken, ImmutableArray<InterpolatedStringContentSyntax> contents)
            : base(syntaxTree)
        {
            InterpolatedToken = interpolatedToken;
            Contents = contents;
        }

        public override SyntaxKind Kind => SyntaxKind.InterpolatedStringExpression;

        public SyntaxToken InterpolatedToken { get; }
        public ImmutableArray<InterpolatedStringContentSyntax> Contents { get; }
    }
}
