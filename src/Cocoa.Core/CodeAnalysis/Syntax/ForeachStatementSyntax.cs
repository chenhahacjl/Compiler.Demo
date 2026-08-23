namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// foreach 循环：`foreach [var] x in collection`，括号与 `var` 均可选。
    /// </summary>
    public sealed partial class ForeachStatementSyntax : StatementSyntax
    {
        internal ForeachStatementSyntax(SyntaxTree syntaxTree, SyntaxToken keyword, SyntaxToken? openParenToken, SyntaxToken? varKeyword, SyntaxToken identifier, SyntaxToken inKeyword, ExpressionSyntax collection, SyntaxToken? closeParenToken, StatementSyntax body)
            : base(syntaxTree)
        {
            Keyword = keyword;
            OpenParenToken = openParenToken;
            VarKeyword = varKeyword;
            Identifier = identifier;
            InKeyword = inKeyword;
            Collection = collection;
            CloseParenToken = closeParenToken;
            Body = body;
        }

        public override SyntaxKind Kind => SyntaxKind.ForeachStatement;

        public SyntaxToken Keyword { get; }
        public SyntaxToken? OpenParenToken { get; }
        public SyntaxToken? VarKeyword { get; }
        public SyntaxToken Identifier { get; }
        public SyntaxToken InKeyword { get; }
        public ExpressionSyntax Collection { get; }
        public SyntaxToken? CloseParenToken { get; }
        public StatementSyntax Body { get; }
    }
}
