using System.Collections.Immutable;

namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// with 表达式：`expr with { 成员 = 值, ... }`（C# 9，record 非破坏复制）。
    /// 绑定阶段脱糖为接收者类的构造器调用（位置字段原值透传 + with 赋值覆盖）。
    /// </summary>
    public sealed partial class WithExpressionSyntax : ExpressionSyntax
    {
        internal WithExpressionSyntax(SyntaxTree syntaxTree, ExpressionSyntax expression, SyntaxToken withKeyword, SyntaxToken openBraceToken, SeparatedSyntaxList<ExpressionSyntax> assignments, SyntaxToken closeBraceToken)
            : base(syntaxTree)
        {
            Expression = expression;
            WithKeyword = withKeyword;
            OpenBraceToken = openBraceToken;
            Assignments = assignments;
            CloseBraceToken = closeBraceToken;
        }

        public override SyntaxKind Kind => SyntaxKind.WithExpression;

        public ExpressionSyntax Expression { get; }
        public SyntaxToken WithKeyword { get; }
        public SyntaxToken OpenBraceToken { get; }
        public SeparatedSyntaxList<ExpressionSyntax> Assignments { get; }
        public SyntaxToken CloseBraceToken { get; }

        public override IEnumerable<SyntaxNode> GetChildren()
        {
            yield return Expression;
            yield return WithKeyword;
            yield return OpenBraceToken;
            foreach (var child in Assignments.GetWithSeparators())
            {
                yield return child;
            }
            yield return CloseBraceToken;
        }
    }
}
