namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>插值洞 <c>{expr}</c>。</summary>
    public sealed partial class InterpolationSyntax : InterpolatedStringContentSyntax
    {
        internal InterpolationSyntax(SyntaxTree syntaxTree, ExpressionSyntax expression)
            : base(syntaxTree)
        {
            Expression = expression;
        }

        public override SyntaxKind Kind => SyntaxKind.Interpolation;

        public ExpressionSyntax Expression { get; }
    }
}
