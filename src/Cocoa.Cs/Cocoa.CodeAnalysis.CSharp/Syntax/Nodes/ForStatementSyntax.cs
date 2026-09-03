using System.Collections.Immutable;

using Cocoa.CodeAnalysis.Syntax;

namespace Cocoa.CodeAnalysis.CSharp.Syntax
{
    /// <summary>
    /// C# 风格 for 循环 `for (init; cond; update) body`，三段均可省略；
    /// init 可为变量声明（<see cref="InitDeclaration"/>）或逗号分隔的初始化表达式列表
    /// （<see cref="Initializers"/>，如 `i = 0, j = 0`）；update 为逗号分隔的更新表达式列表
    /// （<see cref="Incrementors"/>，如 `i++, j--`）。
    /// </summary>
    public sealed partial class ForStatementSyntax : StatementSyntax
    {
        internal ForStatementSyntax(
            SyntaxTree syntaxTree,
            SyntaxToken keyword,
            SyntaxToken? openParenToken,
            VariableDeclarationSyntax? initDeclaration,
            SeparatedSyntaxList<ExpressionSyntax> initializers,
            SyntaxToken? semicolonToken1,
            ExpressionSyntax? condition,
            SyntaxToken? semicolonToken2,
            SeparatedSyntaxList<ExpressionSyntax> incrementors,
            SyntaxToken? closeParenToken,
            StatementSyntax body)
            : base(syntaxTree)
        {
            Keyword = keyword;
            OpenParenToken = openParenToken;
            InitDeclaration = initDeclaration;
            Initializers = initializers ?? new SeparatedSyntaxList<ExpressionSyntax>(ImmutableArray<SyntaxNode>.Empty);
            SemicolonToken1 = semicolonToken1;
            Condition = condition;
            SemicolonToken2 = semicolonToken2;
            Incrementors = incrementors ?? new SeparatedSyntaxList<ExpressionSyntax>(ImmutableArray<SyntaxNode>.Empty);
            CloseParenToken = closeParenToken;
            Body = body;
        }

        public override CSharpSyntaxKind Kind => CSharpSyntaxKind.ForStatement;

        public SyntaxToken Keyword { get; }
        public SyntaxToken? OpenParenToken { get; }

        /// <summary>init 为变量声明形式（`int i = 0` / `var i = 0`）；否则为 null。</summary>
        public VariableDeclarationSyntax? InitDeclaration { get; }

        /// <summary>init 为逗号分隔的初始化表达式列表（`i = 0, j = 0`）；变量声明形式时为空。</summary>
        public SeparatedSyntaxList<ExpressionSyntax> Initializers { get; }

        public SyntaxToken? SemicolonToken1 { get; }
        public ExpressionSyntax? Condition { get; }
        public SyntaxToken? SemicolonToken2 { get; }

        /// <summary>逗号分隔的更新表达式列表（`i++, j--`）。</summary>
        public SeparatedSyntaxList<ExpressionSyntax> Incrementors { get; }

        public SyntaxToken? CloseParenToken { get; }
        public StatementSyntax Body { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Keyword;
            if (OpenParenToken != null)
            {
                yield return OpenParenToken;
            }
            if (InitDeclaration != null)
            {
                yield return InitDeclaration;
            }
            foreach (var child in Initializers.GetWithSeparators())
            {
                yield return child;
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
            foreach (var child in Incrementors.GetWithSeparators())
            {
                yield return child;
            }
            if (CloseParenToken != null)
            {
                yield return CloseParenToken;
            }
            yield return Body;
        }
    }
}
