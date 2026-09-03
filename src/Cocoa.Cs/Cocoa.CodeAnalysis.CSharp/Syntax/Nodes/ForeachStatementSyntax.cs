using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// foreach 寰幆锛歚foreach [var] x in collection`锛屾嫭鍙蜂笌 `var` 鍧囧彲閫夈€?
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

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.ForeachStatement;

        public SyntaxToken Keyword { get; }
        public SyntaxToken? OpenParenToken { get; }
        public SyntaxToken? VarKeyword { get; }
        public SyntaxToken Identifier { get; }
        public SyntaxToken InKeyword { get; }
        public ExpressionSyntax Collection { get; }
        public SyntaxToken? CloseParenToken { get; }
        public StatementSyntax Body { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Keyword;
            if (OpenParenToken != null)
            {
                yield return OpenParenToken;
            }
            if (VarKeyword != null)
            {
                yield return VarKeyword;
            }
            yield return Identifier;
            yield return InKeyword;
            yield return Collection;
            if (CloseParenToken != null)
            {
                yield return CloseParenToken;
            }
            yield return Body;
        }
    }
}

