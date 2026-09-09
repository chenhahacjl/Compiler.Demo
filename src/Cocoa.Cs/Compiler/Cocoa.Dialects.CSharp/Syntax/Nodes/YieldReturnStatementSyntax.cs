using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    public sealed partial class YieldReturnStatementSyntax : StatementSyntax
    {
        internal YieldReturnStatementSyntax(SyntaxTree syntaxTree, SyntaxToken yieldKeyword,
            SyntaxToken returnKeyword, ExpressionSyntax expression)
            : base(syntaxTree)
        {
            YieldKeyword = yieldKeyword;
            ReturnKeyword = returnKeyword;
            Expression = expression;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.YieldReturnStatement;

        public SyntaxToken YieldKeyword { get; }
        public SyntaxToken ReturnKeyword { get; }
        public ExpressionSyntax Expression { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return YieldKeyword;
            yield return ReturnKeyword;
            yield return Expression;
        }
    }
}
