using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.Cocoa.Syntax
{
    public sealed partial class UsingStatementSyntax : StatementSyntax
    {
        internal UsingStatementSyntax(SyntaxTree syntaxTree, SyntaxToken usingKeyword, SyntaxToken openParenToken,
            StatementSyntax resource, SyntaxToken closeParenToken, BlockStatementSyntax body)
            : base(syntaxTree)
        {
            UsingKeyword = usingKeyword;
            OpenParenToken = openParenToken;
            Resource = resource;
            CloseParenToken = closeParenToken;
            Body = body;
        }

        public override CocoaSyntaxKind Kind => CocoaSyntaxKind.UsingStatement;

        public SyntaxToken UsingKeyword { get; }
        public SyntaxToken OpenParenToken { get; }
        public StatementSyntax Resource { get; }
        public SyntaxToken CloseParenToken { get; }
        public BlockStatementSyntax Body { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return UsingKeyword;
            yield return OpenParenToken;
            yield return Resource;
            yield return CloseParenToken;
            yield return Body;
        }
    }
}
