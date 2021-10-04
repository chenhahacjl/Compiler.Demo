namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 编译单元语法
    /// </summary>
    public sealed class CompilationUnitSyntax : SyntaxNode
    {
        public CompilationUnitSyntax(ExpressionSyntax expression, SyntaxToken endOfFileToken)
        {
            Expression = expression;
            EndOfFileToken = endOfFileToken;
        }

        public override SyntaxKind Kind => SyntaxKind.CompilationUnit;

        public ExpressionSyntax Expression { get; }
        public SyntaxToken EndOfFileToken { get; }
    }
}
