using Cocoa.CodeAnalysis.Syntax;
using Cocoa.CodeAnalysis.Syntax.Green;

namespace Cocoa.CodeAnalysis.Syntax.Nodes
{
    /// <summary>
    /// foreach 循环：`foreach [var] x in collection`，括号与 `var` 均可选。
    /// 支持元组解构变量：`foreach (var (a, b) in pairs)` —— <see cref="Identifier"/> 为空、
    /// <see cref="VariableTuple"/> 为元组表达式（元素为解构目标名）。
    /// </summary>
    public sealed partial class ForeachStatementSyntax : StatementSyntax
    {
        internal ForeachStatementSyntax(SyntaxTree syntaxTree, SyntaxToken keyword, SyntaxToken? openParenToken, SyntaxToken? varKeyword, SyntaxToken identifier, ExpressionSyntax? variableTuple, SyntaxToken inKeyword, ExpressionSyntax collection, SyntaxToken? closeParenToken, StatementSyntax body)
            : base(syntaxTree)
        {
            Keyword = keyword;
            OpenParenToken = openParenToken;
            VarKeyword = varKeyword;
            Identifier = identifier;
            VariableTuple = variableTuple;
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
        public ExpressionSyntax? VariableTuple { get; }
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
            if (VariableTuple != null)
            {
                yield return VariableTuple;
            }
            else
            {
                yield return Identifier;
            }
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

