namespace Cocoa.CodeAnalysis.Syntax
{
    /// <summary>
    /// 成员方法调用表达式语法：`s.substring(1, 3)` / 泛型显式实参 `list.Map&lt;int&gt;(f)`（6e-M20）。
    /// </summary>
    public sealed partial class MemberCallExpressionSyntax : ExpressionSyntax
    {
        internal MemberCallExpressionSyntax(SyntaxTree syntaxTree, ExpressionSyntax expression, SyntaxToken dotToken, SyntaxToken identifierToken, TypeArgumentListSyntax? typeArguments, SyntaxToken openParenthesisToken, SeparatedSyntaxList<ExpressionSyntax> arguments, SyntaxToken closeParenthesisToken)
            : base(syntaxTree)
        {
            Expression = expression;
            DotToken = dotToken;
            IdentifierToken = identifierToken;
            TypeArguments = typeArguments;
            OpenParenthesisToken = openParenthesisToken;
            Arguments = arguments;
            CloseParenthesisToken = closeParenthesisToken;
        }

        public override SyntaxKind Kind => SyntaxKind.MemberCallExpression;

        public ExpressionSyntax Expression { get; }
        public SyntaxToken DotToken { get; }
        public SyntaxToken IdentifierToken { get; }

        /// <summary>泛型类型实参列表（`obj.M<int>(…)`；非泛型调用为 null，6e-M20 首版仅显式实参）。</summary>
        public TypeArgumentListSyntax? TypeArguments { get; }

        public SyntaxToken OpenParenthesisToken { get; }
        public SeparatedSyntaxList<ExpressionSyntax> Arguments { get; }
        public SyntaxToken CloseParenthesisToken { get; }
    }
}
