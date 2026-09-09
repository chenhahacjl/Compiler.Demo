using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    public sealed partial class YieldBreakStatementSyntax : StatementSyntax
    {
        internal YieldBreakStatementSyntax(SyntaxTree syntaxTree, SyntaxToken yieldKeyword,
            SyntaxToken breakKeyword)
            : base(syntaxTree)
        {
            YieldKeyword = yieldKeyword;
            BreakKeyword = breakKeyword;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.YieldBreakStatement;

        public SyntaxToken YieldKeyword { get; }
        public SyntaxToken BreakKeyword { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return YieldKeyword;
            yield return BreakKeyword;
        }
    }
}
