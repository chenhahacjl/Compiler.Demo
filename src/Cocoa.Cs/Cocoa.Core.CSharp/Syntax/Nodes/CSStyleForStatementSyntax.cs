using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// C# 椋庢牸 for 寰幆 `for (init; cond; update) body`锛屼笁涓儴鍒嗗潎鍙渷鐣ャ€?
    /// </summary>
    public sealed partial class CSStyleForStatementSyntax : StatementSyntax
    {
        internal CSStyleForStatementSyntax(
            SyntaxTree syntaxTree,
            SyntaxToken keyword,
            SyntaxToken openParenToken,
            StatementSyntax? init,
            SyntaxToken? semicolonToken1,
            ExpressionSyntax? condition,
            SyntaxToken? semicolonToken2,
            ExpressionSyntax? update,
            SyntaxToken closeParenToken,
            StatementSyntax body)
            : base(syntaxTree)
        {
            Keyword = keyword;
            OpenParenToken = openParenToken;
            Init = init;
            SemicolonToken1 = semicolonToken1;
            Condition = condition;
            SemicolonToken2 = semicolonToken2;
            Update = update;
            CloseParenToken = closeParenToken;
            Body = body;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.CSStyleForStatement;

        public SyntaxToken Keyword { get; }
        public SyntaxToken OpenParenToken { get; }
        public StatementSyntax? Init { get; }
        public SyntaxToken? SemicolonToken1 { get; }
        public ExpressionSyntax? Condition { get; }
        public SyntaxToken? SemicolonToken2 { get; }
        public ExpressionSyntax? Update { get; }
        public SyntaxToken CloseParenToken { get; }
        public StatementSyntax Body { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Keyword;
            yield return OpenParenToken;
            if (Init != null)
            {
                yield return Init;
            }
            if (SemicolonToken1 != null)
            {
                yield return SemicolonToken1;
            }
            if (Condition != null)
            {
                yield return Condition;
            }
            if (SemicolonToken2 != null)
            {
                yield return SemicolonToken2;
            }
            if (Update != null)
            {
                yield return Update;
            }
            yield return CloseParenToken;
            yield return Body;
        }
    }
}

